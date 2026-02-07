using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class RepositoryWatcherService : IRepositoryWatcherService, IDisposable
{
	private readonly ConcurrentDictionary<string, WatchRegistration> _watchTargets = new(StringComparer.OrdinalIgnoreCase);
	private readonly ILogger<RepositoryWatcherService> _logger;
	private bool _isDisposed;

	public RepositoryWatcherService(ILogger<RepositoryWatcherService> logger)
	{
		_logger = logger;
	}

	public event EventHandler<RepositoryChangedEventArgs>? RepositoryChanged;

	public void Register(string repositoryId, string watchPath)
	{
		if (string.IsNullOrWhiteSpace(repositoryId) || string.IsNullOrWhiteSpace(watchPath))
		{
			return;
		}

		ThrowIfDisposed();

		var normalizedId = repositoryId.Trim();
		var normalizedPath = NormalizeDirectoryPath(watchPath);
		if (normalizedPath is null)
		{
			_logger.LogWarning("Watcher registration skipped. repositoryId={RepositoryId}, watchPath={WatchPath}", normalizedId, watchPath);
			return;
		}

		var registration = CreateRegistration(normalizedId, normalizedPath);
		_watchTargets.AddOrUpdate(
			normalizedId,
			registration,
			(_, existing) =>
			{
				existing.Dispose();
				return registration;
			});

		_logger.LogInformation("Watcher registered. repositoryId={RepositoryId}, watchPath={WatchPath}", normalizedId, normalizedPath);
	}

	public void Unregister(string repositoryId)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return;
		}

		ThrowIfDisposed();
		if (_watchTargets.TryRemove(repositoryId.Trim(), out var registration))
		{
			registration.Dispose();
		}

		_logger.LogInformation("Watcher placeholder unregistered. repositoryId={RepositoryId}", repositoryId);
	}

	public void Dispose()
	{
		if (_isDisposed)
		{
			return;
		}

		foreach (var entry in _watchTargets)
		{
			entry.Value.Dispose();
		}

		_watchTargets.Clear();
		_isDisposed = true;
	}

	private WatchRegistration CreateRegistration(string repositoryId, string watchPath)
	{
		var watcher = new FileSystemWatcher(watchPath)
		{
			IncludeSubdirectories = true,
			NotifyFilter = NotifyFilters.FileName
				| NotifyFilters.DirectoryName
				| NotifyFilters.LastWrite
				| NotifyFilters.CreationTime,
			InternalBufferSize = 16 * 1024,
			EnableRaisingEvents = false
		};

		FileSystemEventHandler changedHandler = (_, args) => RaiseChanged(repositoryId, args.FullPath, args.ChangeType.ToString());
		RenamedEventHandler renamedHandler = (_, args) => RaiseChanged(repositoryId, args.FullPath, WatcherChangeTypes.Renamed.ToString());
		ErrorEventHandler errorHandler = (_, args) =>
		{
			_logger.LogError(args.GetException(), "Watcher error. repositoryId={RepositoryId}, watchPath={WatchPath}", repositoryId, watchPath);
			RaiseChanged(repositoryId, watchPath, "Error");
		};

		watcher.Changed += changedHandler;
		watcher.Created += changedHandler;
		watcher.Deleted += changedHandler;
		watcher.Renamed += renamedHandler;
		watcher.Error += errorHandler;
		watcher.EnableRaisingEvents = true;

		return new WatchRegistration(watcher, changedHandler, renamedHandler, errorHandler);
	}

	private void RaiseChanged(string repositoryId, string fullPath, string changeType)
	{
		if (IsGitInternalPath(fullPath))
		{
			return;
		}

		_logger.LogDebug(
			"Repository change detected. repositoryId={RepositoryId}, changeType={ChangeType}, path={Path}",
			repositoryId,
			changeType,
			fullPath);
		RepositoryChanged?.Invoke(this, new RepositoryChangedEventArgs(repositoryId));
	}

	private static string? NormalizeDirectoryPath(string path)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				return null;
			}

			var normalized = Path.GetFullPath(path.Trim());
			if (!Directory.Exists(normalized))
			{
				return null;
			}

			return normalized;
		}
		catch
		{
			return null;
		}
	}

	private static bool IsGitInternalPath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		var normalized = path.Replace('/', Path.DirectorySeparatorChar);
		var gitSegment = $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}";
		return normalized.Contains(gitSegment, StringComparison.OrdinalIgnoreCase)
			|| normalized.EndsWith($"{Path.DirectorySeparatorChar}.git", StringComparison.OrdinalIgnoreCase);
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_isDisposed, this);
	}

	private sealed class WatchRegistration : IDisposable
	{
		private readonly FileSystemWatcher _watcher;
		private readonly FileSystemEventHandler _changedHandler;
		private readonly RenamedEventHandler _renamedHandler;
		private readonly ErrorEventHandler _errorHandler;
		private bool _disposed;

		public WatchRegistration(
			FileSystemWatcher watcher,
			FileSystemEventHandler changedHandler,
			RenamedEventHandler renamedHandler,
			ErrorEventHandler errorHandler)
		{
			_watcher = watcher;
			_changedHandler = changedHandler;
			_renamedHandler = renamedHandler;
			_errorHandler = errorHandler;
		}

		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_watcher.EnableRaisingEvents = false;
			_watcher.Changed -= _changedHandler;
			_watcher.Created -= _changedHandler;
			_watcher.Deleted -= _changedHandler;
			_watcher.Renamed -= _renamedHandler;
			_watcher.Error -= _errorHandler;
			_watcher.Dispose();
			_disposed = true;
		}
	}
}
