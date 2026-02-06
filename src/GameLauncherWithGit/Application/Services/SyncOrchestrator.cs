using GameLauncherWithGit.Application.Abstractions;
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
	private readonly ILogger<SyncOrchestrator> _logger;
	private readonly ConcurrentDictionary<string, RepositorySyncQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
	private readonly TimeSpan _debounceDuration = TimeSpan.FromSeconds(10);
	private bool _isDisposed;

	public SyncOrchestrator(
		IRepositoryStateStore repositoryStateStore,
		IRepositoryWatcherService repositoryWatcherService,
		IGitService gitService,
		ILogger<SyncOrchestrator> logger)
	{
		_repositoryStateStore = repositoryStateStore;
		_repositoryWatcherService = repositoryWatcherService;
		_gitService = gitService;
		_logger = logger;
		_repositoryWatcherService.RepositoryChanged += OnRepositoryChanged;
	}

	public Task QueueRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return Task.CompletedTask;
		}

		ThrowIfDisposed();

		var normalizedId = repositoryId.Trim();
		var queue = _queues.GetOrAdd(normalizedId, static _ => new RepositorySyncQueue());
		CancellationTokenSource debounceCts;

		lock (queue.Gate)
		{
			if (queue.IsSyncRunning)
			{
				queue.RerunRequested = true;
				_logger.LogDebug("Sync is running; rerun requested. repositoryId={RepositoryId}", normalizedId);
				return Task.CompletedTask;
			}

			queue.DebounceCts?.Cancel();
			queue.DebounceCts?.Dispose();
			debounceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			queue.DebounceCts = debounceCts;
		}

		_repositoryStateStore.SetState(normalizedId, RepositorySyncState.Debouncing);
		_ = DebounceAndRunAsync(normalizedId, queue, debounceCts);
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
		CancellationTokenSource debounceCts)
	{
		try
		{
			await Task.Delay(_debounceDuration, debounceCts.Token);
		}
		catch (OperationCanceledException)
		{
			return;
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

		try
		{
			_repositoryStateStore.SetState(repositoryId, RepositorySyncState.Syncing);
			await ExecuteSyncCoreAsync(repositoryId, debounceCts.Token);
			_repositoryStateStore.SetState(repositoryId, RepositorySyncState.Idle);
		}
		catch (OperationCanceledException)
		{
			_repositoryStateStore.SetState(repositoryId, RepositorySyncState.Idle);
		}
		catch (SyncCommandException ex)
		{
			_repositoryStateStore.SetState(
				repositoryId,
				ex.ShouldPauseRepository ? RepositorySyncState.ErrorPaused : RepositorySyncState.Idle);
			_logger.LogError(ex, "Repository sync command failed. repositoryId={RepositoryId}", repositoryId);
		}
		catch (Exception ex)
		{
			_repositoryStateStore.SetState(repositoryId, RepositorySyncState.ErrorPaused);
			_logger.LogError(ex, "Repository sync failed. repositoryId={RepositoryId}", repositoryId);
		}
		finally
		{
			var shouldRerun = false;
			lock (queue.Gate)
			{
				queue.IsSyncRunning = false;
				if (queue.RerunRequested)
				{
					queue.RerunRequested = false;
					shouldRerun = true;
				}
			}

			debounceCts.Dispose();
			if (shouldRerun)
			{
				_ = QueueRepositorySyncAsync(repositoryId);
			}
		}
	}

	private async Task ExecuteSyncCoreAsync(string repositoryId, CancellationToken cancellationToken)
	{
		var repositoryPath = repositoryId;
		if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
		{
			throw new SyncCommandException(
				repositoryPath,
				"validate-path",
				"監視対象のリポジトリパスが存在しません。",
				shouldPauseRepository: false);
		}

		await EnsureCommandSuccessAsync(repositoryPath, "fetch", shouldPauseRepository: false, cancellationToken);

		var pullResult = await _gitService.RunAsync(repositoryPath, "pull --rebase --autostash", cancellationToken);
		if (!pullResult.IsSuccess)
		{
			throw new SyncCommandException(
				repositoryPath,
				"pull --rebase --autostash",
				BuildFailureReason(pullResult),
				shouldPauseRepository: IsPullConflict(pullResult));
		}

		await EnsureCommandSuccessAsync(repositoryPath, "add -A", shouldPauseRepository: false, cancellationToken);
		var statusResult = await _gitService.RunAsync(repositoryPath, "status --porcelain", cancellationToken);
		if (!statusResult.IsSuccess)
		{
			throw new SyncCommandException(
				repositoryPath,
				"status --porcelain",
				BuildFailureReason(statusResult),
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
					shouldPauseRepository: false);
			}
		}

		await EnsureCommandSuccessAsync(repositoryPath, "push", shouldPauseRepository: false, cancellationToken);
		_logger.LogInformation("Repository sync placeholder completed. repositoryId={RepositoryId}", repositoryId);
	}

	private async Task EnsureCommandSuccessAsync(
		string repositoryPath,
		string command,
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

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);
	}

	private sealed class RepositorySyncQueue
	{
		public object Gate { get; } = new();

		public CancellationTokenSource? DebounceCts { get; set; }

		public bool IsSyncRunning { get; set; }

		public bool RerunRequested { get; set; }
	}

	private sealed class SyncCommandException : Exception
	{
		public SyncCommandException(
			string repositoryPath,
			string command,
			string reason,
			bool shouldPauseRepository)
			: base($"同期に失敗しました。repo={repositoryPath}, command=git {command}, reason={reason}")
		{
			ShouldPauseRepository = shouldPauseRepository;
		}

		public bool ShouldPauseRepository { get; }
	}
}
