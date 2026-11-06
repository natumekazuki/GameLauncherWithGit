using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioScreenshotStore
{
    Task<ScenarioScreenshot> SaveScreenshotAsync(ScreenshotTarget target, ScreenshotImageData image, DateTimeOffset capturedAt);
    Task<IReadOnlyList<ScenarioScreenshot>> GetEventScreenshotsAsync(long captureGameId, CaptureEventKey key);
    Task ExportScenarioArchiveAsync(long captureGameId, ScenarioArchiveSelection selection, string outputPath);
}
