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
	private readonly ConcurrentDictionary<string, RepositorySyncQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
	private bool _isDisposed;

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
		_repositoryWatcherService.RepositoryChanged += OnRepositoryChanged;
		_trayService.SetState(RepositorySyncState.Idle);
	}

	public Task QueueRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default)
	{
		return QueueRepositorySyncCoreAsync(repositoryId, runImmediately: false, cancellationToken);
	}

	public Task ResumeRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return Task.CompletedTask;
		}

		SetRepositoryState(repositoryId.Trim(), RepositorySyncState.Idle);
		return QueueRepositorySyncCoreAsync(repositoryId, runImmediately: true, cancellationToken);
	}

	private Task QueueRepositorySyncCoreAsync(
		string repositoryId,
		bool runImmediately,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return Task.CompletedTask;
		}

		ThrowIfDisposed();

		var normalizedId = repositoryId.Trim();
		var queue = _queues.GetOrAdd(normalizedId, static _ => new RepositorySyncQueue());
		var debounceDuration = GetDebounceDuration();
		CancellationTokenSource debounceCts;

		lock (queue.Gate)
		{
			queue.RetryCts?.Cancel();
			queue.RetryCts?.Dispose();
			queue.RetryCts = null;

			if (queue.IsSyncRunning)
			{
				queue.RerunRequested = true;
				if (runImmediately)
				{
					queue.RerunImmediately = true;
				}

				_logger.LogDebug("Sync is running; rerun requested. repositoryId={RepositoryId}", normalizedId);
				return Task.CompletedTask;
			}

			queue.DebounceCts?.Cancel();
			queue.DebounceCts?.Dispose();
			debounceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			queue.DebounceCts = debounceCts;
		}

		SetRepositoryState(
			normalizedId,
			runImmediately ? RepositorySyncState.Syncing : RepositorySyncState.Debouncing);
		_ = DebounceAndRunAsync(normalizedId, queue, debounceCts, runImmediately, debounceDuration);
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		_repositoryWatcherService.RepositoryChanged -= OnRepositoryChanged;
		foreach (var queue in _queues.Values)
		{
			lock (queue.Gate)
			{
				queue.DebounceCts?.Cancel();
				queue.DebounceCts?.Dispose();
				queue.DebounceCts = null;
				queue.RetryCts?.Cancel();
				queue.RetryCts?.Dispose();
				queue.RetryCts = null;
			}
		}

		_queues.Clear();
		_isDisposed = true;
	}

	private void OnRepositoryChanged(object? sender, RepositoryChangedEventArgs args)
	{
		_ = QueueRepositorySyncAsync(args.RepositoryId);
	}

	private async Task DebounceAndRunAsync(
		string repositoryId,
		RepositorySyncQueue queue,
		CancellationTokenSource debounceCts,
		bool runImmediately,
		TimeSpan debounceDuration)
	{
		if (!runImmediately)
		{
			try
			{
				await Task.Delay(debounceDuration, debounceCts.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}

		lock (queue.Gate)
		{
			if (!ReferenceEquals(queue.DebounceCts, debounceCts))
			{
				return;
			}

			queue.DebounceCts = null;
			queue.IsSyncRunning = true;
		}

		var syncStartedAt = DateTimeOffset.Now;
		try
		{
			SetRepositoryState(repositoryId, RepositorySyncState.Syncing);
			await ExecuteSyncCoreAsync(repositoryId, syncStartedAt, debounceCts.Token);
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
				await _notificationService.NotifyAsync(
					"同期が復旧しました",
					$"一時的な同期エラーから復旧しました。repo={repositoryId}");
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
			var shouldRerun = false;
			var rerunImmediately = false;
			lock (queue.Gate)
			{
				queue.IsSyncRunning = false;
				if (queue.RerunRequested)
				{
					queue.RerunRequested = false;
					rerunImmediately = queue.RerunImmediately;
					queue.RerunImmediately = false;
					shouldRerun = true;
				}
			}

			debounceCts.Dispose();
			if (shouldRerun)
			{
				_ = QueueRepositorySyncCoreAsync(repositoryId, rerunImmediately, CancellationToken.None);
			}
		}
	}

	private async Task ExecuteSyncCoreAsync(
		string repositoryId,
		DateTimeOffset startedAt,
		CancellationToken cancellationToken)
	{
		var repositoryPath = repositoryId;
		if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
		{
			throw new SyncCommandException(
				repositoryPath,
				"validate-path",
				"監視対象のリポジトリパスが存在しません。",
				startedAt,
				shouldPauseRepository: false);
		}

		await EnsureCommandSuccessAsync(repositoryPath, "fetch", startedAt, shouldPauseRepository: false, cancellationToken);

		var tracking = await TryGetTrackingInfoAsync(repositoryPath, startedAt, cancellationToken);
		if (tracking is not null)
		{
			var pullCommand = $"pull --rebase --autostash {tracking.RemoteName} {tracking.MergeTarget}";
			var pullResult = await _gitService.RunAsync(repositoryPath, pullCommand, cancellationToken);
			if (!pullResult.IsSuccess)
			{
				throw new SyncCommandException(
					repositoryPath,
					pullCommand,
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

		await EnsureCommandSuccessAsync(repositoryPath, "add -A", startedAt, shouldPauseRepository: false, cancellationToken);
		var statusResult = await _gitService.RunAsync(repositoryPath, "status --porcelain", cancellationToken);
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
				$"commit -m \"auto: save sync {timestamp}\"",
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

		await EnsureCommandSuccessAsync(repositoryPath, "push", startedAt, shouldPauseRepository: false, cancellationToken);
		_logger.LogInformation("Repository sync completed. repositoryId={RepositoryId}", repositoryId);
	}

	private async Task EnsureCommandSuccessAsync(
		string repositoryPath,
		string command,
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
			command,
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
		return FirstNonEmptyLine(result.StandardError)
			?? FirstNonEmptyLine(result.StandardOutput)
			?? $"exit code: {result.ExitCode}";
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
		var branchResult = await _gitService.RunAsync(repositoryPath, "branch --show-current", cancellationToken);
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

		var remoteResult = await _gitService.RunAsync(
			repositoryPath,
			$"config --get branch.{branchName}.remote",
			cancellationToken);
		if (!remoteResult.IsSuccess)
		{
			if (IsMissingGitConfigValue(remoteResult))
			{
				return null;
			}

			throw new SyncCommandException(
				repositoryPath,
				$"config --get branch.{branchName}.remote",
				BuildFailureReason(remoteResult),
				startedAt,
				shouldPauseRepository: true);
		}

		var remoteName = FirstNonEmptyLine(remoteResult.StandardOutput);
		if (string.IsNullOrWhiteSpace(remoteName))
		{
			return null;
		}

		var mergeResult = await _gitService.RunAsync(
			repositoryPath,
			$"config --get branch.{branchName}.merge",
			cancellationToken);
		if (!mergeResult.IsSuccess)
		{
			if (IsMissingGitConfigValue(mergeResult))
			{
				return null;
			}

			throw new SyncCommandException(
				repositoryPath,
				$"config --get branch.{branchName}.merge",
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
			queue.RetryCts?.Cancel();
			queue.RetryCts?.Dispose();
			queue.RetryCts = null;
			return hadFailure;
		}
	}

	private void ResetTransientFailureState(RepositorySyncQueue queue)
	{
		lock (queue.Gate)
		{
			queue.ConsecutiveTransientFailures = 0;
			queue.RetryCts?.Cancel();
			queue.RetryCts?.Dispose();
			queue.RetryCts = null;
		}
	}

	private void ScheduleRetry(string repositoryId, RepositorySyncQueue queue, RetryPlan retryPlan)
	{
		if (_isDisposed)
		{
			return;
		}

		CancellationTokenSource retryCts;
		lock (queue.Gate)
		{
			queue.RetryCts?.Cancel();
			queue.RetryCts?.Dispose();
			retryCts = new CancellationTokenSource();
			queue.RetryCts = retryCts;
		}

		_ = RetryAfterDelayAsync(repositoryId, queue, retryCts, retryPlan);
	}

	private async Task RetryAfterDelayAsync(
		string repositoryId,
		RepositorySyncQueue queue,
		CancellationTokenSource retryCts,
		RetryPlan retryPlan)
	{
		try
		{
			try
			{
				await Task.Delay(retryPlan.Delay, retryCts.Token);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			finally
			{
				lock (queue.Gate)
				{
					if (ReferenceEquals(queue.RetryCts, retryCts))
					{
						queue.RetryCts = null;
					}
				}
			}

			if (_isDisposed)
			{
				return;
			}

			_logger.LogInformation(
				"Retrying repository sync. repositoryId={RepositoryId}, failureCount={FailureCount}",
				repositoryId,
				retryPlan.FailureCount);
			await QueueRepositorySyncCoreAsync(repositoryId, runImmediately: true, CancellationToken.None);
		}
		catch (ObjectDisposedException)
		{
			// Disposeと競合した場合は無視する。
		}
		finally
		{
			retryCts.Dispose();
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

		public CancellationTokenSource? DebounceCts { get; set; }

		public CancellationTokenSource? RetryCts { get; set; }

		public bool IsSyncRunning { get; set; }

		public bool RerunRequested { get; set; }

		public bool RerunImmediately { get; set; }

		public int ConsecutiveTransientFailures { get; set; }
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
