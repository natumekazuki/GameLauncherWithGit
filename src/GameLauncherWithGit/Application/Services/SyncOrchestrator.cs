using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Domain.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace GameLauncherWithGit.Application.Services;

public sealed partial class SyncOrchestrator : ISyncOrchestrator, IDisposable
{
	private readonly IRepositoryStateStore _repositoryStateStore;
	private readonly IRepositoryWatcherService _repositoryWatcherService;
	private readonly IGitService _gitService;
	private readonly IAppSettingsService _appSettingsService;
	private readonly INotificationService _notificationService;
	private readonly ITrayService _trayService;
	private readonly ILogAccessService _logAccessService;
	private readonly IRepositorySyncHistoryStore _repositorySyncHistoryStore;
	private readonly ILogger<SyncOrchestrator> _logger;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly TimeProvider _clock;
	private readonly IGameLibraryStore? _gameLibraryStore;
	private readonly Func<string, string>? _resolveDirectoryPath;
	private readonly ConcurrentDictionary<string, RepositorySyncQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
	private volatile bool _isDisposed;

	public SyncOrchestrator(
		IRepositoryStateStore repositoryStateStore,
		IRepositoryWatcherService repositoryWatcherService,
		IGitService gitService,
		IAppSettingsService appSettingsService,
		INotificationService notificationService,
		ITrayService trayService,
		ILogAccessService logAccessService,
		IRepositorySyncHistoryStore repositorySyncHistoryStore,
		IGameLibraryStore gameLibraryStore,
		ILogger<SyncOrchestrator> logger)
		: this(repositoryStateStore, repositoryWatcherService, gitService, appSettingsService, notificationService,
			trayService, logAccessService, repositorySyncHistoryStore, logger, Task.Delay,
			gameLibraryStore: gameLibraryStore, resolveDirectoryPath: GitService.ResolveDirectoryPath)
	{
	}

	internal SyncOrchestrator(
		IRepositoryStateStore repositoryStateStore,
		IRepositoryWatcherService repositoryWatcherService,
		IGitService gitService,
		IAppSettingsService appSettingsService,
		INotificationService notificationService,
		ITrayService trayService,
		ILogAccessService logAccessService,
		IRepositorySyncHistoryStore repositorySyncHistoryStore,
		ILogger<SyncOrchestrator> logger,
		Func<TimeSpan, CancellationToken, Task> delay,
		TimeProvider? clock = null,
		IGameLibraryStore? gameLibraryStore = null,
		Func<string, string>? resolveDirectoryPath = null)
	{
		_repositoryStateStore = repositoryStateStore;
		_repositoryWatcherService = repositoryWatcherService;
		_gitService = gitService;
		_appSettingsService = appSettingsService;
		_notificationService = notificationService;
		_trayService = trayService;
		_logAccessService = logAccessService;
		_repositorySyncHistoryStore = repositorySyncHistoryStore;
		_logger = logger;
		_delay = delay;
		_clock = clock ?? TimeProvider.System;
		_gameLibraryStore = gameLibraryStore;
		_resolveDirectoryPath = resolveDirectoryPath;
		_repositoryWatcherService.RepositoryChanged += OnRepositoryChanged;
		_repositoryWatcherService.RepositoryRegistered += OnRepositoryRegistered;
		_trayService.SetState(RepositorySyncState.Idle);
	}

	public Task QueueRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default)
	{
		TryQueueRepositorySync(repositoryId, SyncRequest.Changed, cancellationToken);
		return Task.CompletedTask;
	}

	public bool TryQueueRepositorySyncNow(string repositoryId) =>
		TryQueueRepositorySync(repositoryId, SyncRequest.Manual, CancellationToken.None);

	public Task ResumeRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default)
	{
		TryQueueRepositorySync(repositoryId, SyncRequest.Resume, cancellationToken);
		return Task.CompletedTask;
	}

	private bool TryQueueRepositorySync(
		string repositoryId,
		SyncRequest request,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return false;
		}

		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();

		var normalizedId = NormalizeRepositoryKey(repositoryId, _resolveDirectoryPath);
		var queue = _queues.GetOrAdd(normalizedId, static _ => new RepositorySyncQueue());
		SyncReservation? reservation;
		lock (queue.Gate)
		{
			if (!TryReserveLocked(normalizedId, queue, request, cancellationToken, out reservation))
			{
				return false;
			}
		}

		StartReservation(normalizedId, queue, reservation);
		return true;
	}

	private bool TryReserveLocked(
		string repositoryId,
		RepositorySyncQueue queue,
		SyncRequest request,
		CancellationToken cancellationToken,
		out SyncReservation? reservation,
		bool replay = false)
	{
		reservation = null;
		var currentState = queue.Pending is { Token.IsCancellationRequested: true } canceled
			? canceled.StateOnCancel
			: _repositoryStateStore.GetStateOrDefault(repositoryId);
		var isPaused = currentState == RepositorySyncState.ErrorPaused;
		// Only an explicit request made while stopped can release that stop.
		if (request == SyncRequest.Resume && !isPaused)
		{
			request = SyncRequest.Manual;
		}
		if (_isDisposed || cancellationToken.IsCancellationRequested || (isPaused && request != SyncRequest.Resume))
		{
			return false;
		}

		var settings = _appSettingsService.Get();
		var useCadence = queue.HasCadenceWork || queue.RunningCadence || IsCadenceEnabled(settings);
		if (request == SyncRequest.Registered && !useCadence)
		{
			return false;
		}

		var now = _clock.GetUtcNow();
		if (request == SyncRequest.Changed && useCadence && !replay)
		{
			queue.FirstChangeAt ??= now;
			queue.ChangeReadyAt = now + GetDebounceDuration();
		}

		var forced = request is SyncRequest.Manual or SyncRequest.Resume;
		if (!forced && useCadence && queue.RetryCts is not null)
		{
			// A watcher or cadence deadline cannot preempt retry backoff for the incoming/commit steps.
			return true;
		}

		CancelRetryLocked(queue);
		if (queue.IsSyncRunning)
		{
			// Registering the same repository again during a run does not request another copy of that run.
			if (request != SyncRequest.Registered && (queue.Rerun is null || request > queue.Rerun))
			{
				queue.Rerun = request;
			}
			return true;
		}

		var previous = queue.Pending;
		if (previous is not null && !previous.Token.IsCancellationRequested
			&& (request < previous.Request || (request == SyncRequest.Registered)))
		{
			// This run has not started yet; it already covers a new request for the same repository.
			return true;
		}

		var wait = request == SyncRequest.Changed ? GetDebounceDuration() : TimeSpan.Zero;
		if (request == SyncRequest.Changed && useCadence && queue.ChangeReadyAt is { } ready)
		{
			wait = ready - now;
			if (settings.AutoCommitIntervalMinutes > 0 && queue.FirstChangeAt is { } firstChange)
			{
				var deadlineWait = firstChange.AddMinutes(settings.AutoCommitIntervalMinutes) - now;
				if (deadlineWait < wait) wait = deadlineWait;
			}
			if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
		}

		reservation = new SyncReservation(request, wait,
			previous?.StateOnCancel ?? currentState, cancellationToken);
		queue.Pending = reservation;
		_repositoryStateStore.SetState(repositoryId,
			wait == TimeSpan.Zero ? RepositorySyncState.Syncing : RepositorySyncState.Debouncing);
		previous?.Cancellation.Cancel();
		return true;
	}

	private void StartReservation(string repositoryId, RepositorySyncQueue queue, SyncReservation? reservation)
	{
		if (reservation is null)
		{
			return;
		}

		_trayService.SetState(GetAggregateState());
		_ = RunReservationAsync(repositoryId, queue, reservation);
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_isDisposed = true;
		_repositoryWatcherService.RepositoryChanged -= OnRepositoryChanged;
		_repositoryWatcherService.RepositoryRegistered -= OnRepositoryRegistered;
		foreach (var queue in _queues.Values)
		{
			lock (queue.Gate)
			{
				var pending = queue.Pending;
				queue.Pending = null;
				pending?.Cancellation.Cancel();
				queue.Active?.Cancellation.Cancel();
				CancelRetryLocked(queue);
				CancelCadenceWakeLocked(queue);
			}
		}

		_queues.Clear();
	}

	private void OnRepositoryChanged(object? sender, RepositoryChangedEventArgs args)
	{
		if (!_isDisposed)
		{
			TryQueueRepositorySync(args.RepositoryId, SyncRequest.Changed, CancellationToken.None);
		}
	}

	private void OnRepositoryRegistered(object? sender, RepositoryChangedEventArgs args)
	{
		if (!_isDisposed)
		{
			TryQueueRepositorySync(args.RepositoryId, SyncRequest.Registered, CancellationToken.None);
		}
	}

	private async Task RunReservationAsync(
		string repositoryId,
		RepositorySyncQueue queue,
		SyncReservation reservation)
	{
		try
		{
			if (reservation.DebounceDuration > TimeSpan.Zero)
			{
				await _delay(reservation.DebounceDuration, reservation.Token);
			}

			DateTimeOffset? firstChangeAt;
			DateTimeOffset? commitNotBefore;
			AppSettings settings;
			bool useCadence;
			lock (queue.Gate)
			{
				if (_isDisposed || !ReferenceEquals(queue.Pending, reservation) || reservation.Token.IsCancellationRequested)
				{
					return;
				}

				queue.Pending = null;
				queue.Active = reservation;
				queue.IsSyncRunning = true;
				settings = _appSettingsService.Get().Normalize();
				useCadence = queue.HasCadenceWork || IsCadenceEnabled(settings);
				queue.RunningCadence = useCadence;
				firstChangeAt = queue.FirstChangeAt;
				var now = _clock.GetUtcNow();
				commitNotBefore = useCadence && reservation.Request is not (SyncRequest.Manual or SyncRequest.Resume)
					&& queue.ChangeReadyAt is { } ready && ready > now ? ready : null;
				if (commitNotBefore is null || (settings.AutoCommitIntervalMinutes > 0
					&& firstChangeAt?.AddMinutes(settings.AutoCommitIntervalMinutes) <= now))
				{
					queue.FirstChangeAt = null;
					queue.ChangeReadyAt = null;
				}
				CancelCadenceWakeLocked(queue);
			}

			await RunSyncAsync(repositoryId, queue, reservation.Request, settings, useCadence, firstChangeAt,
				commitNotBefore, reservation.Token);
		}
		catch (OperationCanceledException) when (reservation.Token.IsCancellationRequested)
		{
			// A canceled wait has not started any Git operations.
		}
		finally
		{
			var stateChanged = false;
			lock (queue.Gate)
			{
				if (ReferenceEquals(queue.Active, reservation))
				{
					queue.Active = null;
				}

				if (ReferenceEquals(queue.Pending, reservation))
				{
					queue.Pending = null;
					_repositoryStateStore.SetState(repositoryId, reservation.StateOnCancel);
					stateChanged = true;
				}

				reservation.Cancellation.Dispose();
			}

			if (stateChanged && !_isDisposed)
			{
				_trayService.SetState(GetAggregateState());
			}
		}
	}

	private async Task RunSyncAsync(string repositoryId, RepositorySyncQueue queue, SyncRequest request,
		AppSettings settings, bool useCadence, DateTimeOffset? firstChangeAt, DateTimeOffset? commitNotBefore,
		CancellationToken cancellationToken)
	{
		var syncStartedAt = DateTimeOffset.Now;
		try
		{
			SetRepositoryState(repositoryId, RepositorySyncState.Syncing);
			var force = request is SyncRequest.Manual or SyncRequest.Resume;
			if ((useCadence || !force) && !await CheckRegisteredAsync(repositoryId, syncStartedAt, cancellationToken))
			{
				queue.Cadence = new();
				lock (queue.Gate)
				{
					queue.FirstChangeAt = null;
					queue.ChangeReadyAt = null;
				}
				ResetTransientFailureState(queue);
				return;
			}

			if (useCadence)
			{
				if (!await ExecuteCadenceAsync(repositoryId, queue.Cadence, settings, force, firstChangeAt,
					commitNotBefore, syncStartedAt, cancellationToken))
				{
					return;
				}
			}
			else
			{
				await ExecuteSyncCoreAsync(repositoryId, syncStartedAt, cancellationToken);
			}
			await AppendSyncHistorySafeAsync(
				repositoryId,
				RepositorySyncHistoryStatus.Succeeded,
				syncStartedAt,
				DateTimeOffset.Now,
				command: null,
				reason: null);
			var hadTransientFailure = ClearTransientFailureState(queue) || queue.Cadence.ReportPushRecovery;
			queue.Cadence.ReportPushRecovery = false;
			SetRepositoryState(repositoryId, RepositorySyncState.Idle);
			if (hadTransientFailure)
			{
				try
				{
					await _notificationService.NotifyAsync(
						"同期が復旧しました",
						$"一時的な同期エラーから復旧しました。repo={repositoryId}");
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Failed to notify sync recovery. repositoryId={RepositoryId}", repositoryId);
				}
			}
		}
		catch (OperationCanceledException)
		{
			SetRepositoryState(repositoryId, RepositorySyncState.Idle);
		}
		catch (SyncCommandException ex)
		{
			var finishedAt = DateTimeOffset.Now;
			var targetState = ex.ShouldPauseRepository ? RepositorySyncState.ErrorPaused : RepositorySyncState.Idle;
			SetRepositoryState(repositoryId, targetState);
			await AppendSyncHistorySafeAsync(
				repositoryId,
				ex.ShouldPauseRepository ? RepositorySyncHistoryStatus.Paused : RepositorySyncHistoryStatus.Failed,
				syncStartedAt,
				finishedAt,
				ex.Command,
				ex.Reason);
			if (ex.ShouldPauseRepository)
			{
				ResetTransientFailureState(queue);
				_logger.LogError(ex, "Repository sync command failed. repositoryId={RepositoryId}", repositoryId);
				await NotifyAndLogErrorAsync(
					"自動同期を停止しました",
					$"競合または致命的なエラーにより同期を停止しました。repo={repositoryId} / {ex.Message}");
			}
			else
			{
				var retryPlan = useCadence && ex.IsPushStep
					? CreateRetryPlan(++queue.Cadence.PushFailures)
					: RegisterTransientFailure(queue);
				_logger.LogWarning(
					ex,
					"Repository sync transient failure. repositoryId={RepositoryId}, failureCount={FailureCount}, retryDelaySeconds={RetryDelaySeconds}",
					repositoryId,
					retryPlan.FailureCount,
					(int)retryPlan.Delay.TotalSeconds);

				var message =
					$"同期に失敗しました。repo={repositoryId}, {retryPlan.FailureCount}回目, {Math.Ceiling(retryPlan.Delay.TotalSeconds)}秒後に自動再試行します。 / {ex.Message}";
				if (retryPlan.ShouldNotify)
				{
					await NotifyAndLogErrorAsync("同期に失敗しました", message);
				}
				else
				{
					await AppendErrorAsync($"再試行継続中: {message}");
				}

				if (useCadence && ex.IsPushStep)
				{
					queue.Cadence.PushRetryAt = _clock.GetUtcNow() + retryPlan.Delay;
				}
				else
				{
					if (useCadence && firstChangeAt is not null && queue.Cadence.CommitSince is null)
					{
						queue.Cadence.CommitSince = firstChangeAt;
					}
					ScheduleRetry(repositoryId, queue, retryPlan);
				}
			}
		}
		catch (Exception ex)
		{
			var finishedAt = DateTimeOffset.Now;
			ResetTransientFailureState(queue);
			SetRepositoryState(repositoryId, RepositorySyncState.ErrorPaused);
			await AppendSyncHistorySafeAsync(
				repositoryId,
				RepositorySyncHistoryStatus.Paused,
				syncStartedAt,
				finishedAt,
				command: "unknown",
				reason: ex.Message);
			_logger.LogError(ex, "Repository sync failed. repositoryId={RepositoryId}", repositoryId);
			await NotifyAndLogErrorAsync(
				"自動同期を停止しました",
				$"予期しないエラーで同期を停止しました。repo={repositoryId} / {ex.Message}");
		}
		finally
		{
			SyncReservation? reservation = null;
			CadenceWake? wake;
			var stateChanged = false;
			lock (queue.Gate)
			{
				queue.IsSyncRunning = false;
				queue.RunningCadence = false;
				queue.HasCadenceWork = queue.Cadence.HasPending || queue.ChangeReadyAt is not null;
				if (_repositoryStateStore.GetStateOrDefault(repositoryId) == RepositorySyncState.Syncing)
				{
					_repositoryStateStore.SetState(repositoryId, RepositorySyncState.Idle);
					stateChanged = true;
				}

				if (queue.Rerun is { } rerun)
				{
					queue.Rerun = null;
					TryReserveLocked(repositoryId, queue, rerun, CancellationToken.None, out reservation, replay: true);
				}
				wake = useCadence && !cancellationToken.IsCancellationRequested
					? ReserveCadenceWakeLocked(repositoryId, queue, settings) : null;
			}

			if (stateChanged && reservation is null && !_isDisposed)
			{
				_trayService.SetState(GetAggregateState());
			}
			StartReservation(repositoryId, queue, reservation);
			if (wake is not null)
			{
				_ = WakeAtDeadlineAsync(repositoryId, queue, wake.Delay, wake.Source, wake.Token);
			}
		}
	}

	private async Task ExecuteSyncCoreAsync(
		string repositoryId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken)
	{
		var repositoryPath = repositoryId;
		if (string.IsNullOrWhiteSpace(repositoryPath))
		{
			throw new SyncCommandException(
				repositoryPath,
				"validate-path",
				"監視対象のリポジトリパスが設定されていません。",
				startedAt,
				shouldPauseRepository: false);
		}

		var hasTracking = await PrepareIncomingAsync(repositoryPath, startedAt, cancellationToken);
		await CommitChangesAsync(repositoryPath, startedAt, cancellationToken);
		if (!hasTracking)
		{
			_logger.LogInformation(
				"Pull tracking is not configured. Continue push with git default destination. repositoryPath={RepositoryPath}",
				repositoryPath);
		}

		await EnsureCommandSuccessAsync(repositoryPath, ["push"], startedAt, shouldPauseRepository: false, cancellationToken);
		_logger.LogInformation("Repository sync completed. repositoryId={RepositoryId}", repositoryId);
	}

	private async Task<bool> PrepareIncomingAsync(string repositoryPath, DateTimeOffset startedAt, CancellationToken cancellationToken)
	{
		await EnsureCommandSuccessAsync(repositoryPath, ["fetch"], startedAt, shouldPauseRepository: false, cancellationToken);

		var tracking = await TryGetTrackingInfoAsync(repositoryPath, startedAt, cancellationToken);
		if (tracking is not null)
		{
			string[] pullCommand = ["pull", "--rebase", "--autostash", tracking.RemoteName, tracking.MergeTarget];
			var pullResult = await RunGitAsync(repositoryPath, pullCommand, cancellationToken);
			if (!pullResult.IsSuccess)
			{
				throw new SyncCommandException(
					repositoryPath,
					string.Join(' ', pullCommand),
					BuildFailureReason(pullResult),
					startedAt,
					shouldPauseRepository: IsPullConflict(pullResult) || IsPullConfigurationError(pullResult));
			}
		}
		else
		{
			_logger.LogInformation(
				"Skip pull because upstream is not configured. repositoryPath={RepositoryPath}",
				repositoryPath);
		}

		return tracking is not null;
	}

	private async Task CommitChangesAsync(string repositoryPath, DateTimeOffset startedAt, CancellationToken cancellationToken)
	{
		await EnsureCommandSuccessAsync(repositoryPath, ["add", "-A"], startedAt, shouldPauseRepository: false, cancellationToken);
		var statusResult = await RunGitAsync(repositoryPath, ["status", "--porcelain"], cancellationToken);
		if (!statusResult.IsSuccess)
		{
			throw new SyncCommandException(
				repositoryPath,
				"status --porcelain",
				BuildFailureReason(statusResult),
				startedAt,
				shouldPauseRepository: false);
		}

		if (!string.IsNullOrWhiteSpace(statusResult.StandardOutput))
		{
			var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
			var commitResult = await RunGitAsync(
				repositoryPath,
				["commit", "-m", $"auto: save sync {timestamp}"],
				cancellationToken);
			if (!commitResult.IsSuccess && !IsNothingToCommit(commitResult))
			{
				throw new SyncCommandException(
					repositoryPath,
					"commit -m",
					BuildFailureReason(commitResult),
					startedAt,
					shouldPauseRepository: false);
			}
		}
	}

	private async Task<GitCommandResult> RunGitAsync(
		string repositoryPath, IReadOnlyList<string> command, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var result = await _gitService.RunAsync(repositoryPath, command, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		return result;
	}

	private async Task EnsureCommandSuccessAsync(
		string repositoryPath,
		IReadOnlyList<string> command,
		DateTimeOffset startedAt,
		bool shouldPauseRepository,
		CancellationToken cancellationToken)
	{
		var result = await RunGitAsync(repositoryPath, command, cancellationToken);
		if (result.IsSuccess)
		{
			return;
		}

		throw new SyncCommandException(
			repositoryPath,
			string.Join(' ', command),
			BuildFailureReason(result),
			startedAt,
			shouldPauseRepository);
	}

	private static bool IsPullConflict(GitCommandResult result)
	{
		var text = $"{result.StandardError}\n{result.StandardOutput}";
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		return text.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("Resolve all conflicts manually", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("could not apply", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("fix conflicts and then run", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("競合", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsPullConfigurationError(GitCommandResult result)
	{
		var text = $"{result.StandardError}\n{result.StandardOutput}";
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		return text.Contains("Cannot rebase onto multiple branches", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("There is no tracking information for the current branch", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("no upstream configured", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("upstream branch", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("Please specify which branch you want to rebase against", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("Updating an unborn branch with changes added to the index", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsNothingToCommit(GitCommandResult result)
	{
		var text = $"{result.StandardError}\n{result.StandardOutput}";
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		return text.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("no changes added to commit", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("作業ツリーはクリーン", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("コミットするものがありません", StringComparison.OrdinalIgnoreCase);
	}

	private static string BuildFailureReason(GitCommandResult result)
	{
		return result.GetFailureReason();
	}

	private static bool IsMissingGitConfigValue(GitCommandResult result)
	{
		if (result.IsSuccess)
		{
			return false;
		}

		return result.ExitCode == 1
			&& string.IsNullOrWhiteSpace(result.StandardError)
			&& string.IsNullOrWhiteSpace(result.StandardOutput);
	}

	private async Task<TrackingInfo?> TryGetTrackingInfoAsync(
		string repositoryPath,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken)
	{
		var branchResult = await RunGitAsync(repositoryPath, ["branch", "--show-current"], cancellationToken);
		if (!branchResult.IsSuccess)
		{
			throw new SyncCommandException(
				repositoryPath,
				"branch --show-current",
				BuildFailureReason(branchResult),
				startedAt,
				shouldPauseRepository: true);
		}

		var branchName = FirstNonEmptyLine(branchResult.StandardOutput);
		if (string.IsNullOrWhiteSpace(branchName))
		{
			throw new SyncCommandException(
				repositoryPath,
				"branch --show-current",
				"現在のブランチを特定できません。detached HEAD を解除してから同期してください。",
				startedAt,
				shouldPauseRepository: true);
		}

		var remoteConfigKey = BuildBranchConfigKey(branchName, "remote");
		var remoteResult = await RunGitAsync(
			repositoryPath,
			["config", "--get", remoteConfigKey],
			cancellationToken);
		if (!remoteResult.IsSuccess)
		{
			if (IsMissingGitConfigValue(remoteResult))
			{
				return null;
			}

			throw new SyncCommandException(
				repositoryPath,
				$"config --get {remoteConfigKey}",
				BuildFailureReason(remoteResult),
				startedAt,
				shouldPauseRepository: true);
		}

		var remoteName = FirstNonEmptyLine(remoteResult.StandardOutput);
		if (string.IsNullOrWhiteSpace(remoteName))
		{
			return null;
		}

		var mergeConfigKey = BuildBranchConfigKey(branchName, "merge");
		var mergeResult = await RunGitAsync(
			repositoryPath,
			["config", "--get", mergeConfigKey],
			cancellationToken);
		if (!mergeResult.IsSuccess)
		{
			if (IsMissingGitConfigValue(mergeResult))
			{
				return null;
			}

			throw new SyncCommandException(
				repositoryPath,
				$"config --get {mergeConfigKey}",
				BuildFailureReason(mergeResult),
				startedAt,
				shouldPauseRepository: true);
		}

		var mergeTarget = NormalizeMergeTarget(FirstNonEmptyLine(mergeResult.StandardOutput) ?? string.Empty);
		if (string.IsNullOrWhiteSpace(mergeTarget))
		{
			return null;
		}

		return new TrackingInfo(remoteName, mergeTarget);
	}

	private static string BuildBranchConfigKey(string branchName, string key)
	{
		return $"branch.{branchName}.{key}";
	}

	private static string NormalizeMergeTarget(string mergeTarget)
	{
		const string HeadsPrefix = "refs/heads/";
		var normalized = mergeTarget.Trim();
		return normalized.StartsWith(HeadsPrefix, StringComparison.OrdinalIgnoreCase)
			? normalized[HeadsPrefix.Length..]
			: normalized;
	}

	private static string? FirstNonEmptyLine(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		return value
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
	}

	private void SetRepositoryState(string repositoryId, RepositorySyncState state)
	{
		_repositoryStateStore.SetState(repositoryId, state);
		_trayService.SetState(GetAggregateState());
	}

	private RepositorySyncState GetAggregateState()
	{
		var snapshot = _repositoryStateStore.Snapshot();
		if (snapshot.Values.Any(static state => state == RepositorySyncState.ErrorPaused))
		{
			return RepositorySyncState.ErrorPaused;
		}

		if (snapshot.Values.Any(static state => state is RepositorySyncState.Syncing or RepositorySyncState.Debouncing))
		{
			return RepositorySyncState.Syncing;
		}

		return RepositorySyncState.Idle;
	}

	private async Task NotifyAndLogErrorAsync(string title, string message)
	{
		try
		{
			await _notificationService.NotifyAsync(title, message);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to notify sync error. title={Title}", title);
		}

		try
		{
			await AppendErrorAsync($"{title}: {message}");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to append sync error log.");
		}
	}

	private Task AppendErrorAsync(string message)
	{
		return _logAccessService.AppendErrorAsync(message);
	}

	private RetryPlan RegisterTransientFailure(RepositorySyncQueue queue)
	{
		lock (queue.Gate)
		{
			queue.ConsecutiveTransientFailures++;
			return CreateRetryPlan(queue.ConsecutiveTransientFailures);
		}
	}

	private RetryPlan CreateRetryPlan(int failureCount) =>
		new(failureCount, CalculateRetryDelay(failureCount), ShouldNotify: failureCount == 1);

	private bool ClearTransientFailureState(RepositorySyncQueue queue)
	{
		lock (queue.Gate)
		{
			var hadFailure = queue.ConsecutiveTransientFailures > 0;
			queue.ConsecutiveTransientFailures = 0;
			CancelRetryLocked(queue);
			return hadFailure;
		}
	}

	private void ResetTransientFailureState(RepositorySyncQueue queue)
	{
		lock (queue.Gate)
		{
			queue.ConsecutiveTransientFailures = 0;
			CancelRetryLocked(queue);
		}
	}

	private static void CancelRetryLocked(RepositorySyncQueue queue)
	{
		var retry = queue.RetryCts;
		queue.RetryCts = null;
		retry?.Cancel();
	}

	private void ScheduleRetry(string repositoryId, RepositorySyncQueue queue, RetryPlan retryPlan)
	{
		CancellationTokenSource retryCts;
		CancellationToken retryToken;
		lock (queue.Gate)
		{
			if (_isDisposed)
			{
				return;
			}

			CancelRetryLocked(queue);
			retryCts = new CancellationTokenSource();
			retryToken = retryCts.Token;
			queue.RetryCts = retryCts;
		}

		_ = RetryAfterDelayAsync(repositoryId, queue, retryCts, retryToken, retryPlan);
	}

	private async Task RetryAfterDelayAsync(
		string repositoryId,
		RepositorySyncQueue queue,
		CancellationTokenSource retryCts,
		CancellationToken retryToken,
		RetryPlan retryPlan)
	{
		try
		{
			await _delay(retryPlan.Delay, retryToken);
			SyncReservation? reservation;
			lock (queue.Gate)
			{
				if (!ReferenceEquals(queue.RetryCts, retryCts) || retryToken.IsCancellationRequested)
				{
					return;
				}

				queue.RetryCts = null;
				if (!TryReserveLocked(repositoryId, queue, SyncRequest.Retry, CancellationToken.None, out reservation))
				{
					return;
				}
			}

			_logger.LogInformation(
				"Retrying repository sync. repositoryId={RepositoryId}, failureCount={FailureCount}",
				repositoryId,
				retryPlan.FailureCount);
			StartReservation(repositoryId, queue, reservation);
		}
		catch (OperationCanceledException) when (retryToken.IsCancellationRequested)
		{
			// A newer request or shutdown superseded this retry.
		}
		finally
		{
			lock (queue.Gate)
			{
				if (ReferenceEquals(queue.RetryCts, retryCts))
				{
					queue.RetryCts = null;
				}

				retryCts.Dispose();
			}
		}
	}

	private TimeSpan CalculateRetryDelay(int failureCount)
	{
		var settings = _appSettingsService.Get();
		var initialRetrySeconds = Math.Max(1, settings.SyncRetryInitialSeconds);
		var maxRetrySeconds = Math.Max(initialRetrySeconds, settings.SyncRetryMaxSeconds);
		var exponent = Math.Max(0, failureCount - 1);
		var multiplier = Math.Pow(2, exponent);
		var seconds = Math.Min(maxRetrySeconds, initialRetrySeconds * multiplier);
		return TimeSpan.FromSeconds(Math.Max(1, seconds));
	}

	private TimeSpan GetDebounceDuration()
	{
		var settings = _appSettingsService.Get();
		var seconds = Math.Max(1, settings.SyncDebounceSeconds);
		return TimeSpan.FromSeconds(seconds);
	}

	private async Task AppendSyncHistorySafeAsync(
		string repositoryId,
		RepositorySyncHistoryStatus status,
		DateTimeOffset startedAt,
		DateTimeOffset finishedAt,
		string? command,
		string? reason)
	{
		try
		{
			var normalizedFinishedAt = finishedAt < startedAt ? startedAt : finishedAt;
			var durationMs = Math.Max(0L, (long)(normalizedFinishedAt - startedAt).TotalMilliseconds);
			await _repositorySyncHistoryStore.AppendAsync(
				new RepositorySyncHistoryItem(
					Id: 0,
					RepositoryId: repositoryId,
					Status: status,
					StartedAt: startedAt,
					FinishedAt: normalizedFinishedAt,
					DurationMs: durationMs,
					Command: command,
					Reason: reason));
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to append sync history. repositoryId={RepositoryId}", repositoryId);
		}
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);
	}

	private sealed class RepositorySyncQueue
	{
		public object Gate { get; } = new();

		public SyncReservation? Pending { get; set; }
		public SyncReservation? Active { get; set; }

		public CancellationTokenSource? RetryCts { get; set; }

		public bool IsSyncRunning { get; set; }

		public SyncRequest? Rerun { get; set; }
		public DateTimeOffset? FirstChangeAt { get; set; }
		public DateTimeOffset? ChangeReadyAt { get; set; }
		public bool HasCadenceWork { get; set; }
		public bool RunningCadence { get; set; }
		public CadenceWork Cadence { get; set; } = new();
		public CancellationTokenSource? CadenceCts { get; set; }

		public int ConsecutiveTransientFailures { get; set; }
	}

	// All reservations are installed and canceled under the queue lock. Only their worker disposes the source.
	private sealed class SyncReservation
	{
		public SyncReservation(SyncRequest request, TimeSpan debounceDuration,
			RepositorySyncState stateOnCancel, CancellationToken cancellationToken)
		{
			Request = request;
			DebounceDuration = debounceDuration;
			StateOnCancel = stateOnCancel;
			Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			Token = Cancellation.Token;
		}

		public SyncRequest Request { get; }
		public TimeSpan DebounceDuration { get; }
		public RepositorySyncState StateOnCancel { get; }
		public CancellationTokenSource Cancellation { get; }
		public CancellationToken Token { get; }
	}

	private sealed record RetryPlan(
		int FailureCount,
		TimeSpan Delay,
		bool ShouldNotify);

	private sealed record TrackingInfo(
		string RemoteName,
		string MergeTarget);

	private sealed class SyncCommandException : Exception
	{
		public SyncCommandException(
			string repositoryPath,
			string command,
			string reason,
			DateTimeOffset startedAt,
			bool shouldPauseRepository,
			bool isPushStep = false,
			bool isGitStep = true)
			: base($"同期に失敗しました。repo={repositoryPath}, {(isGitStep ? $"command=git {command}" : $"step={command}")}, reason={reason}")
		{
			Command = command;
			Reason = reason;
			StartedAt = startedAt;
			ShouldPauseRepository = shouldPauseRepository;
			IsPushStep = isPushStep;
		}

		public string Command { get; }

		public string Reason { get; }

		public DateTimeOffset StartedAt { get; }

		public bool ShouldPauseRepository { get; }
		public bool IsPushStep { get; }
	}

	// If requests coalesce, deadlines/retries outrank ordinary detection and explicit operations outrank automation.
	private enum SyncRequest { Changed, Registered, Due, Retry, Manual, Resume }
}
