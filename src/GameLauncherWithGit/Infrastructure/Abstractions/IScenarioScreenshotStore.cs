using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioScreenshotStore
{
    Task<ScenarioScreenshot> SaveScreenshotAsync(ScreenshotTarget target, ScreenshotImageData image, DateTimeOffset capturedAt, CancellationToken cancellationToken = default);
    /// <summary>Lists attachments for an existing event in the given game. Missing events and wrong owners fail; a valid event may have no attachments.</summary>
    Task<IReadOnlyList<ScenarioScreenshot>> GetEventScreenshotsAsync(long captureGameId, CaptureEventKey key);
    Task<byte[]> ReadEventScreenshotPngAsync(long captureGameId, CaptureEventKey key, string imageId);
    /// <summary>Reads only event/image metadata. Valid events without attachments return an empty image list; invalid references fail.</summary>
    Task<IReadOnlyList<ScenarioScreenshotEventMetadata>> GetEventScreenshotMetadataAsync(long captureGameId,
        IReadOnlyCollection<CaptureEventKey> events, CancellationToken cancellationToken = default);
    /// <summary>Counts attachments of each requested event, including zero. A missing event or wrong owner fails the read.</summary>
    Task<IReadOnlyDictionary<CaptureEventKey, int>> GetScreenshotCountsAsync(long captureGameId, IReadOnlyCollection<CaptureEventKey> events);
    Task<ScreenshotCleanupResult> CleanupUnreferencedScreenshotsAsync();
    Task<byte[]> ReadScenarioImageAsync(ScenarioMaterialAccessLevel level, ScenarioMaterialImageRequest request,
        CancellationToken cancellationToken = default);
    Task ExportScenarioArchiveAsync(long captureGameId, ScenarioArchiveSelection selection, string outputPath,
        ScenarioMaterialAccessLevel accessLevel, CancellationToken cancellationToken = default);
}
