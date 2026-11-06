using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioScreenshotPlatform : IDisposable
{
    IReadOnlyList<CaptureWindow> ListWindows(int? processId = null);
    Task<ScenarioScreenshotFrame> CaptureAsync(CaptureWindow target, CancellationToken cancellationToken = default);
    Task RegisterHotkeyAsync(ScreenshotHotkey hotkey, Action callback);
    Task UnregisterHotkeyAsync();
}
