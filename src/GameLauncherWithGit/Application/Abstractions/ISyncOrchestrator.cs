namespace GameLauncherWithGit.Application.Abstractions;

public interface ISyncOrchestrator
{
	Task QueueRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default);

	Task ResumeRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default);
}
