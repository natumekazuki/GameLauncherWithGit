using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class RepositoryWatcherService : IRepositoryWatcherService
{
	private readonly ConcurrentDictionary<string, string> _watchTargets = new(StringComparer.OrdinalIgnoreCase);
	private readonly ILogger<RepositoryWatcherService> _logger;

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

		_watchTargets[repositoryId] = watchPath;
		_logger.LogInformation("Watcher placeholder registered. repositoryId={RepositoryId}, watchPath={WatchPath}", repositoryId, watchPath);
	}

	public void Unregister(string repositoryId)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return;
		}

		_watchTargets.TryRemove(repositoryId, out _);
		_logger.LogInformation("Watcher placeholder unregistered. repositoryId={RepositoryId}", repositoryId);
	}

	internal void RaiseChanged(string repositoryId)
	{
		RepositoryChanged?.Invoke(this, new RepositoryChangedEventArgs(repositoryId));
	}
}
