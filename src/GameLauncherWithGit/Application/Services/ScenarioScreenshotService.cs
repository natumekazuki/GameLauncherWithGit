using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Application.Services;

public sealed record ScreenshotStatus(string Status, bool Busy, bool Registered, ScreenshotHotkey Hotkey,
    CaptureWindow? Window, bool ManualWindow, long SavedCount);

public sealed class ScenarioScreenshotService : IAsyncDisposable
{
    private readonly ScenarioCaptureService _capture;
    private readonly IScenarioScreenshotStore _store;
    private readonly IScenarioScreenshotPlatform _platform;
    private readonly IAppSettingsService _settings;
    private readonly ILogger<ScenarioScreenshotService> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _registration = new(1, 1);
    private ScreenshotStatus _snapshot;
    private long? _windowSession;
    private int _capturing;
    private bool _disposed;

    public event Action? Changed;
    public ScreenshotStatus Snapshot { get { lock (_gate) return _snapshot; } }

    public ScenarioScreenshotService(ScenarioCaptureService capture, IScenarioScreenshotStore store,
        IScenarioScreenshotPlatform platform, IAppSettingsService settings, ILogger<ScenarioScreenshotService> logger)
    {
        _capture = capture; _store = store; _platform = platform; _settings = settings; _logger = logger;
        var config = settings.Get();
        _snapshot = new("正式記録中に撮影できます", false, false,
            new(config.ScreenshotModifiers, config.ScreenshotVirtualKey), null, false, 0);
        capture.StateChanged += OnCaptureStateChanged;
    }

    public IReadOnlyList<CaptureWindow> GetWindows() => _platform.ListWindows();

    public void SelectWindow(CaptureWindow? window)
    {
        var capture = _capture.Snapshot;
        if (!capture.Recording) throw new InvalidOperationException("正式記録を開始してから撮影対象を選択してください。");
        if (window is not null && !_platform.ListWindows().Any(x => x.Handle == window.Handle && x.ProcessId == window.ProcessId))
            throw new InvalidOperationException("撮影対象のウィンドウが見つかりません。再読込してください。");
        lock (_gate)
        {
            _windowSession = capture.SessionId;
            _snapshot = _snapshot with { Window = window, ManualWindow = window is not null, Status = "撮影対象を設定しました" };
        }
        Changed?.Invoke();
    }

    public async Task ConfigureHotkeyAsync(ScreenshotHotkey hotkey)
    {
        ValidateHotkey(hotkey);
        await _registration.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _platform.UnregisterHotkeyAsync();
            Update(s => s with { Registered = false });
            if (_capture.Snapshot.Recording)
            {
                await _platform.RegisterHotkeyAsync(hotkey, OnHotkey);
                Update(s => s with { Registered = true });
            }
            await _settings.SaveAsync(_settings.Get() with { ScreenshotModifiers = hotkey.Modifiers, ScreenshotVirtualKey = hotkey.VirtualKey });
            Update(s => s with { Hotkey = hotkey, Status = s.Registered ? "撮影キーを登録しました" : "撮影キーを保存しました" });
        }
        catch (Exception ex)
        {
            await _platform.UnregisterHotkeyAsync();
            Update(s => s with { Registered = false, Status = ex.Message });
            throw;
        }
        finally { _registration.Release(); }
    }

    private static void ValidateHotkey(ScreenshotHotkey key)
    {
        if (key.Modifiers == 0 || (key.Modifiers & ~7u) != 0
            || key.VirtualKey is not (>= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7A))
            throw new ArgumentException("Ctrl/Alt/Shiftと英数字またはF1～F11を指定してください。");
    }

    private void OnCaptureStateChanged() => _ = RefreshRegistrationAsync();
    private void OnHotkey() => _ = CaptureAsync();

    private async Task RefreshRegistrationAsync()
    {
        await _registration.WaitAsync();
        try
        {
            if (_disposed) return;
            var capture = _capture.Snapshot;
            lock (_gate)
            {
                if (_windowSession != capture.SessionId || !capture.Recording)
                {
                    _windowSession = capture.Recording ? capture.SessionId : null;
                    _snapshot = _snapshot with { Window = null, ManualWindow = false };
                }
            }
            if (capture.Recording && !Snapshot.Registered)
            {
                ValidateHotkey(Snapshot.Hotkey);
                await _platform.RegisterHotkeyAsync(Snapshot.Hotkey, OnHotkey);
                Update(s => s with { Registered = true, Status = "撮影キーを登録しました" });
            }
            else if (!capture.Recording)
            {
                await _platform.UnregisterHotkeyAsync();
                Update(s => s with { Registered = false, Status = "正式記録中に撮影できます" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Screenshot hotkey registration failed");
            Update(s => s with { Registered = false, Status = ex.Message });
        }
        finally { _registration.Release(); }
    }

    public async Task CaptureAsync()
    {
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
        {
            Update(s => s with { Status = "撮影を保存中です。完了後にもう一度押してください。" });
            return;
        }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Freeze the body identity before any asynchronous capture/encoding work.
            var (target, processId) = _capture.FreezeScreenshotTarget();
            var capturedAt = DateTimeOffset.UtcNow;
            CaptureWindow? window;
            lock (_gate) window = _windowSession == target.Event.SessionId && _snapshot.ManualWindow ? _snapshot.Window : null;
            if (window is null)
            {
                var windows = _platform.ListWindows(processId);
                if (windows.Count != 1)
                    throw new InvalidOperationException(windows.Count == 0
                        ? "本文の取得先に撮影できるウィンドウがありません。撮影設定で選択してください。"
                        : "本文の取得先に複数のウィンドウがあります。撮影設定で選択してください。");
                window = windows[0];
            }
            Update(s => s with { Busy = true, Window = window, Status = "撮影を保存中" });
            var frame = await _platform.CaptureAsync(window);
            await _capture.FlushPendingAsync();
            await _store.SaveScreenshotAsync(target, new(frame.Width, frame.Height, frame.PixelHash, frame.PngBytes), capturedAt);
            Update(s => s with { SavedCount = s.SavedCount + 1, Status = "スクリーンショットを保存しました" });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Screenshot capture/save failed");
            Update(s => s with { Status = ex.Message });
        }
        finally
        {
            Interlocked.Exchange(ref _capturing, 0);
            Update(s => s with { Busy = false });
        }
    }

    private void Update(Func<ScreenshotStatus, ScreenshotStatus> update)
    {
        lock (_gate) _snapshot = update(_snapshot);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _capture.StateChanged -= OnCaptureStateChanged;
        await _registration.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _platform.UnregisterHotkeyAsync();
            Update(s => s with { Registered = false });
        }
        finally { _registration.Release(); }
    }
}
