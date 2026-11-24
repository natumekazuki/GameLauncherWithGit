using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Domain.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace GameLauncherWithGit.Application.Services;

public sealed class SyncOrchestrator : ISyncOrchestrator, IDisposable
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
		ILogger<SyncOrchestrator> logger)
		: this(repositoryStateStore, repositoryWatcherService, gitService, appSettingsService, notificationService,
			trayService, logAccessService, repositorySyncHistoryStore, logger, Task.Delay)
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
		Func<TimeSpan, CancellationToken, Task> delay)
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
		_repositoryWatcherService.RepositoryChanged += OnRepositoryChanged;
		_trayService.SetState(RepositorySyncState.Idle);
	}

	public Task QueueRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default)
	{
		TryQueueRepositorySync(repositoryId, runImmediately: false, resumePaused: false, cancellationToken);
		return Task.CompletedTask;
	}

	public bool TryQueueRepositorySyncNow(string repositoryId) =>
		TryQueueRepositorySync(repositoryId, runImmediately: true, resumePaused: false, CancellationToken.None);

	public Task ResumeRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default)
	{
		TryQueueRepositorySync(repositoryId, runImmediately: true, resumePaused: true, cancellationToken);
		return Task.CompletedTask;
	}

	private bool TryQueueRepositorySync(
		string repositoryId,
		bool runImmediately,
		bool resumePaused,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return false;
		}

		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();

		var normalizedId = repositoryId.Trim();
		var queue = _queues.GetOrAdd(normalizedId, static _ => new RepositorySyncQueue());
		SyncReservation? reservation;
		lock (queue.Gate)
		{
			if (!TryReserveLocked(normalizedId, queue, runImmediately, resumePaused, cancellationToken, out reservation))
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
		bool runImmediately,
		bool resumePaused,
		CancellationToken cancellationToken,
		out SyncReservation? reservation)
	{
		reservation = null;
		var currentState = queue.Pending is { Token.IsCancellationRequested: true } canceled
			? canceled.StateOnCancel
			: _repositoryStateStore.GetStateOrDefault(repositoryId);
		var isPaused = currentState == RepositorySyncState.ErrorPaused;
		if (_isDisposed || cancellationToken.IsCancellationRequested || (isPaused && !resumePaused))
		{
			return false;
		}

		CancelRetryLocked(queue);
		if (queue.IsSyncRunning)
		{
			queue.RerunRequested = true;
			queue.RerunImmediately |= runImmediately;
			queue.RerunResumesPaused |= isPaused && resumePaused;
			return true;
		}

		var previous = queue.Pending;
		if (!runImmediately && previous is { RunImmediately: true } && !previous.Token.IsCancellationRequested)
		{
			// This run has not started yet; it already covers a new request for the same repository.
			return true;
		}

		reservation = new SyncReservation(runImmediately, runImmediately ? TimeSpan.Zero : GetDebounceDuration(),
			previous?.StateOnCancel ?? currentState, cancellationToken);
		queue.Pending = reservation;
		_repositoryStateStore.SetState(repositoryId,
			runImmediately ? RepositorySyncState.Syncing : RepositorySyncState.Debouncing);
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
		foreach (var queue in _queues.Values)
		{
			lock (queue.Gate)
			{
				var pending = queue.Pending;
				queue.Pending = null;
				pending?.Cancellation.Cancel();
				CancelRetryLocked(queue);
			}
		}

		_queues.Clear();
	}

	private void OnRepositoryChanged(object? sender, RepositoryChangedEventArgs args)
	{
		if (!_isDisposed)
		{
			TryQueueRepositorySync(args.RepositoryId, runImmediately: false, resumePaused: false, CancellationToken.None);
		}
	}

	private async Task RunReservationAsync(
		string repositoryId,
		RepositorySyncQueue queue,
		SyncReservation reservation)
	{
		try
		{
			if (!reservation.RunImmediately)
			{
				await _delay(reservation.DebounceDuration, reservation.Token);
			}

			lock (queue.Gate)
			{
				if (_isDisposed || !ReferenceEquals(queue.Pending, reservation) || reservation.Token.IsCancellationRequested)
				{
					return;
				}

				queue.Pending = null;
				queue.IsSyncRunning = true;
			}

			await RunSyncAsync(repositoryId, queue, reservation.Token);
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

	private async Task RunSyncAsync(string repositoryId, RepositorySyncQueue queue, CancellationToken cancellationToken)
	{
		var syncStartedAt = DateTimeOffset.Now;
		try
		{
			SetRepositoryState(repositoryId, RepositorySyncState.Syncing);
			await ExecuteSyncCoreAsync(repositoryId, syncStartedAt, cancellationToken);
			await AppendSyncHistorySafeAsync(
				repositoryId,
				RepositorySyncHistoryStatus.Succeeded,
				syncStartedAt,
				DateTimeOffset.Now,
				command: null,
				reason: null);
			var hadTransientFailure = ClearTransientFailureState(queue);
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
				var retryPlan = RegisterTransientFailure(queue);
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

				ScheduleRetry(repositoryId, queue, retryPlan);
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
			lock (queue.Gate)
			{
				queue.IsSyncRunning = false;
				if (queue.RerunRequested)
				{
					TryReserveLocked(repositoryId, queue, queue.RerunImmediately, queue.RerunResumesPaused,
						CancellationToken.None, out reservation);
					queue.RerunRequested = false;
					queue.RerunImmediately = false;
					queue.RerunResumesPaused = false;
				}
			}

			StartReservation(repositoryId, queue, reservation);
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

		await EnsureCommandSuccessAsync(repositoryPath, ["fetch"], startedAt, shouldPauseRepository: false, cancellationToken);

		var tracking = await TryGetTrackingInfoAsync(repositoryPath, startedAt, cancellationToken);
		if (tracking is not null)
		{
			string[] pullCommand = ["pull", "--rebase", "--autostash", tracking.RemoteName, tracking.MergeTarget];
			var pullResult = await _gitService.RunAsync(repositoryPath, pullCommand, cancellationToken);
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

		await EnsureCommandSuccessAsync(repositoryPath, ["add", "-A"], startedAt, shouldPauseRepository: false, cancellationToken);
		var statusResult = await _gitService.RunAsync(repositoryPath, ["status", "--porcelain"], cancellationToken);
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
			var commitResult = await _gitService.RunAsync(
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

		if (tracking is null)
		{
			_logger.LogInformation(
				"Pull tracking is not configured. Continue push with git default destination. repositoryPath={RepositoryPath}",
				repositoryPath);
		}

		await EnsureCommandSuccessAsync(repositoryPath, ["push"], startedAt, shouldPauseRepository: false, cancellationToken);
		_logger.LogInformation("Repository sync completed. repositoryId={RepositoryId}", repositoryId);
	}

	private async Task EnsureCommandSuccessAsync(
		string repositoryPath,
		IReadOnlyList<string> command,
		DateTimeOffset startedAt,
		bool shouldPauseRepository,
		CancellationToken cancellationToken)
	{
		var result = await _gitService.RunAsync(repositoryPath, command, cancellationToken);
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
		var branchResult = await _gitService.RunAsync(repositoryPath, ["branch", "--show-current"], cancellationToken);
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
		var remoteResult = await _gitService.RunAsync(
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
		var mergeResult = await _gitService.RunAsync(
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
			var failureCount = queue.ConsecutiveTransientFailures;
			var delay = CalculateRetryDelay(failureCount);
			return new RetryPlan(
				failureCount,
				delay,
				ShouldNotify: failureCount == 1);
		}
	}

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
				if (!TryReserveLocked(repositoryId, queue, runImmediately: true, resumePaused: false,
					CancellationToken.None, out reservation))
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

		public CancellationTokenSource? RetryCts { get; set; }

		public bool IsSyncRunning { get; set; }

		public bool RerunRequested { get; set; }

		public bool RerunImmediately { get; set; }

		public bool RerunResumesPaused { get; set; }

		public int ConsecutiveTransientFailures { get; set; }
	}

	// All reservations are installed and canceled under the queue lock. Only their worker disposes the source.
	private sealed class SyncReservation
	{
		public SyncReservation(bool runImmediately, TimeSpan debounceDuration,
			RepositorySyncState stateOnCancel, CancellationToken cancellationToken)
		{
			RunImmediately = runImmediately;
			DebounceDuration = debounceDuration;
			StateOnCancel = stateOnCancel;
			Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			Token = Cancellation.Token;
		}

		public bool RunImmediately { get; }
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
			bool shouldPauseRepository)
			: base($"同期に失敗しました。repo={repositoryPath}, command=git {command}, reason={reason}")
		{
			Command = command;
			Reason = reason;
			StartedAt = startedAt;
			ShouldPauseRepository = shouldPauseRepository;
		}

		public string Command { get; }

		public string Reason { get; }

		public DateTimeOffset StartedAt { get; }

		public bool ShouldPauseRepository { get; }
	}
}
