using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioMaterialReader
{
    Task<ScenarioMaterialVersion> GetScenarioMaterialVersionAsync(long captureGameId, CancellationToken cancellationToken = default);
    Task<ScenarioMaterialSnapshot> GetScenarioMaterialAsync(long captureGameId, ScenarioArchiveSelection selection,
        CancellationToken cancellationToken = default);
    Task<ScenarioMaterialProjection> GetScenarioProjectionAsync(long captureGameId, ScenarioArchiveSelection selection,
        ScenarioMaterialAccessLevel level, CancellationToken cancellationToken = default);
    Task<ScenarioMatchingBatchResult> ProcessScenarioMatchingBatchAsync(long captureGameId, int batchSize = 32,
        CancellationToken cancellationToken = default);
}
