using GameLauncherWithGit.App.Application.Models;

namespace GameLauncherWithGit.App.Application.Abstractions;

public interface ISyncOrchestrator
{
    void RegisterRepository(RepositorySyncDefinition definition);

    void UnregisterRepository(string repositoryId);

    void RequestSync(string repositoryId, string reason);

    Task RequestImmediateSyncAsync(string repositoryId, CancellationToken cancellationToken = default);
}
