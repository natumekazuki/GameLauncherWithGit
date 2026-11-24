using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioScreenshotPlatform : IDisposable
{
    IReadOnlyList<CaptureWindow> ListWindows(int? processId = null);
    Task<ScenarioScreenshotFrame> CaptureAsync(CaptureWindow target, CancellationToken cancellationToken = default);
    /// <summary>Captures only the target window; never falls back to reading desktop pixels.</summary>
    Task<ScenarioScreenshotFrame> CaptureGameWindowAsync(CaptureWindow target, CancellationToken cancellationToken = default) =>
        Task.FromException<ScenarioScreenshotFrame>(new PlatformNotSupportedException("ゲームウィンドウだけの撮影には対応していません。"));
    Task RegisterHotkeyAsync(ScreenshotHotkey hotkey, Action callback);
    Task UnregisterHotkeyAsync();
    void SignalFailure();
}
