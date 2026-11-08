using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioScreenshotStore
{
    Task<ScenarioScreenshot> SaveScreenshotAsync(ScreenshotTarget target, ScreenshotImageData image, DateTimeOffset capturedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScenarioScreenshot>> GetEventScreenshotsAsync(long captureGameId, CaptureEventKey key);
    Task<IReadOnlyDictionary<CaptureEventKey, int>> GetScreenshotCountsAsync(long captureGameId, IReadOnlyCollection<CaptureEventKey> events);
    Task<ScreenshotCleanupResult> CleanupUnreferencedScreenshotsAsync();
    Task ExportScenarioArchiveAsync(long captureGameId, ScenarioArchiveSelection selection, string outputPath);
}
