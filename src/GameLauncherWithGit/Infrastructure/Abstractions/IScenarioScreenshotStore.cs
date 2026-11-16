using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioScreenshotStore
{
    Task<ScenarioScreenshot> SaveScreenshotAsync(ScreenshotTarget target, ScreenshotImageData image, DateTimeOffset capturedAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScenarioScreenshot>> GetEventScreenshotsAsync(long captureGameId, CaptureEventKey key);
    /// <summary>Reads only event/image metadata. Valid events without attachments return an empty image list; invalid references fail.</summary>
    Task<IReadOnlyList<ScenarioScreenshotEventMetadata>> GetEventScreenshotMetadataAsync(long captureGameId,
        IReadOnlyCollection<CaptureEventKey> events, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<CaptureEventKey, int>> GetScreenshotCountsAsync(long captureGameId, IReadOnlyCollection<CaptureEventKey> events);
    Task<ScreenshotCleanupResult> CleanupUnreferencedScreenshotsAsync();
    Task ExportScenarioArchiveAsync(long captureGameId, ScenarioArchiveSelection selection, string outputPath);
}
