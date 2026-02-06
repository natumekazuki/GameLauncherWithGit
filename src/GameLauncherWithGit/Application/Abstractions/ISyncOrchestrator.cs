namespace GameLauncherWithGit.Application.Abstractions;

public interface ISyncOrchestrator
{
	Task QueueRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default);
}
