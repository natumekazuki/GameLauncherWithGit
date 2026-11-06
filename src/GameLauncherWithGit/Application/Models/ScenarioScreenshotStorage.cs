namespace GameLauncherWithGit.Application.Models;

public sealed record ScreenshotTarget(long CaptureGameId, CaptureEventKey Event);
public sealed record ScreenshotImageData(int Width, int Height, string PixelHash, byte[] Png);
public sealed record ScenarioScreenshot(string ImageId, int Width, int Height, string PixelHash,
    string FilePath, DateTimeOffset CapturedAt);
public sealed record ScenarioArchiveSelection(long? RangeId = null, long? LabelId = null,
    bool UnclassifiedOnly = false, CaptureKind? Kind = null, string? TextSearch = null, string? SpeakerSearch = null);
