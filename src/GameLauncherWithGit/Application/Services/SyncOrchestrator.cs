using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Domain.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Application.Services;

public sealed class SyncOrchestrator : ISyncOrchestrator
{
	private readonly IRepositoryStateStore _repositoryStateStore;
	private readonly ILogger<SyncOrchestrator> _logger;

	public SyncOrchestrator(
		IRepositoryStateStore repositoryStateStore,
		ILogger<SyncOrchestrator> logger)
	{
		_repositoryStateStore = repositoryStateStore;
		_logger = logger;
	}

	public async Task QueueRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return;
		}

		_repositoryStateStore.SetState(repositoryId, RepositorySyncState.Debouncing);
		await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
		_repositoryStateStore.SetState(repositoryId, RepositorySyncState.Syncing);
		await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
		_repositoryStateStore.SetState(repositoryId, RepositorySyncState.Idle);

		_logger.LogInformation("Repository sync placeholder completed. repositoryId={RepositoryId}", repositoryId);
	}
}
