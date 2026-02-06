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
	private readonly ILogger<SyncOrchestrator> _logger;
	private readonly ConcurrentDictionary<string, RepositorySyncQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
	private readonly TimeSpan _debounceDuration = TimeSpan.FromSeconds(10);
	private bool _isDisposed;

	public SyncOrchestrator(
		IRepositoryStateStore repositoryStateStore,
		IRepositoryWatcherService repositoryWatcherService,
		ILogger<SyncOrchestrator> logger)
	{
		_repositoryStateStore = repositoryStateStore;
		_repositoryWatcherService = repositoryWatcherService;
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
		_logger.LogInformation("Repository sync queued event consumed. repositoryId={RepositoryId}", repositoryId);
		await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
		_logger.LogInformation("Repository sync placeholder completed. repositoryId={RepositoryId}", repositoryId);
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
}
