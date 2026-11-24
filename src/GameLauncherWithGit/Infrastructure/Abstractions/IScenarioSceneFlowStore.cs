using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioSceneFlowStore
{
    Task<ScenarioSceneFlowSnapshot> GetScenarioSceneFlowAsync(long captureGameId, CancellationToken cancellationToken = default);
    Task<long> CreateScenarioSceneAsync(long captureGameId, ScenarioSceneEdit edit, CancellationToken cancellationToken = default);
    Task UpdateScenarioSceneAsync(long captureGameId, long sceneId, ScenarioSceneEdit edit, CancellationToken cancellationToken = default);
    Task UpdateDisplayedScenarioSceneAsync(long captureGameId, ScenarioScene displayed, ScenarioSceneEdit edit, CancellationToken cancellationToken = default);
    Task DeleteScenarioSceneAsync(long captureGameId, long sceneId, CancellationToken cancellationToken = default);
    Task SetScenarioSceneConnectionAsync(long captureGameId, long fromSceneId, long toSceneId, bool connected, CancellationToken cancellationToken = default);
    Task SetDisplayedScenarioSceneConnectionAsync(long captureGameId, ScenarioScene from, ScenarioScene to, bool connected, CancellationToken cancellationToken = default);
    Task<long> GetOrCreateScenarioGameTagAsync(long captureGameId, string name, CancellationToken cancellationToken = default);
    Task RenameScenarioGameTagAsync(long captureGameId, long tagId, string name, CancellationToken cancellationToken = default);
    Task DeleteScenarioGameTagAsync(long captureGameId, long tagId, CancellationToken cancellationToken = default);
}
