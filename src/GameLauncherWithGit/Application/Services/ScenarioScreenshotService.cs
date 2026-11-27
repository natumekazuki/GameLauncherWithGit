using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Application.Services;

public sealed record ScreenshotFailure(DateTimeOffset OccurredAt, string Message);
/// <summary>The result of a committed image, tied to its frozen recording event.</summary>
public sealed record ScreenshotSuccess(ScreenshotTarget Target, string ImageId, DateTimeOffset OccurredAt);
public sealed record PlayRecordingScreenshot(string ImageId, int Width, int Height, DateTimeOffset CapturedAt);
public sealed record PlayRecordingScreenshotResult(CaptureEventKey Event, string ImageId);
public sealed record ScreenshotStatus(string Status, bool Busy, bool Registered, ScreenshotHotkey Hotkey,
    CaptureWindow? Window, bool ManualWindow, long SavedCount, ScreenshotFailure? LastFailure = null)
{
    public ScreenshotSuccess? LastSuccess { get; init; }
}

public sealed class ScenarioScreenshotService : IAsyncDisposable
{
    private readonly ScenarioCaptureService _capture;
    private readonly IScenarioScreenshotStore _store;
    private readonly IScenarioScreenshotPlatform _platform;
    private readonly IAppSettingsService _settings;
    private readonly ILogger<ScenarioScreenshotService> _logger;
    private readonly INotificationService _notifications;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _registration = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private ScreenshotStatus _snapshot;
    private long? _windowSession;
    private long? _registeredSession;
    private Task? _activeCapture, _disposeTask;
    private DateTimeOffset? _lastNotificationAt;
    private bool _disposed;

    public event Action? Changed;
    public ScreenshotStatus Snapshot { get { lock (_gate) return _snapshot; } }

    public ScenarioScreenshotService(ScenarioCaptureService capture, IScenarioScreenshotStore store,
        IScenarioScreenshotPlatform platform, IAppSettingsService settings, ILogger<ScenarioScreenshotService> logger,
        INotificationService notifications, TimeProvider? clock = null)
    {
        _capture = capture; _store = store; _platform = platform; _settings = settings; _logger = logger;
        _notifications = notifications; _clock = clock ?? TimeProvider.System;
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
        var previous = Snapshot;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _platform.UnregisterHotkeyAsync();
            Update(s => s with { Registered = false, Status = "撮影キーを変更中" });
            var registered = false;
            var capture = _capture.Snapshot;
            if (capture.Recording)
            {
                await RegisterHotkeyAsync(hotkey, capture);
                registered = true;
            }
            await _settings.SaveAsync(_settings.Get() with { ScreenshotModifiers = hotkey.Modifiers, ScreenshotVirtualKey = hotkey.VirtualKey });
            Update(s => s with { Hotkey = hotkey, Registered = registered, Status = registered ? "撮影キーを登録しました" : "撮影キーを保存しました" });
        }
        catch (Exception ex)
        {
            var restored = false;
            string? restoreFailure = null;
            try
            {
                await _platform.UnregisterHotkeyAsync();
                var capture = _capture.Snapshot;
                if (previous.Registered && capture.Recording && !_disposed)
                {
                    await RegisterHotkeyAsync(previous.Hotkey, capture);
                    restored = true;
                }
            }
            catch (Exception restoreError)
            {
                restoreFailure = restoreError.Message;
                _logger.LogWarning(restoreError, "Previous screenshot hotkey could not be restored");
            }
            Update(s => s with { Hotkey = previous.Hotkey, Registered = restored });
            var recovery = restored
                ? $"以前の {DescribeHotkey(previous.Hotkey)} で撮影できます。"
                : "撮影キーは未登録です。撮影設定で競合しないキーを保存し直してください。";
            var message = $"撮影キーの変更に失敗しました。{ex.Message} {recovery}";
            if (restoreFailure is not null) message += $" 復旧失敗: {restoreFailure}";
            await ReportFailureAsync(message);
            throw new InvalidOperationException(message, ex);
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

    private async Task RegisterHotkeyAsync(ScreenshotHotkey hotkey, CaptureSnapshot capture)
    {
        var profileId = capture.RecordId!.Value;
        var sessionId = capture.SessionId!.Value;
        await _platform.RegisterHotkeyAsync(hotkey, () => _ = CaptureAsync(profileId, sessionId));
        _registeredSession = sessionId;
    }

    private async Task RefreshRegistrationAsync()
    {
        await _registration.WaitAsync();
        try
        {
            if (_disposed) return;
            var capture = _capture.Snapshot;
            lock (_gate)
            {
                if (_windowSession != capture.SessionId || !capture.Active)
                {
                    _windowSession = capture.Recording ? capture.SessionId : null;
                    _snapshot = _snapshot with { Window = null, ManualWindow = false };
                }
            }
            if (capture.Recording && (!Snapshot.Registered || _registeredSession != capture.SessionId))
            {
                ValidateHotkey(Snapshot.Hotkey);
                if (Snapshot.Registered) await _platform.UnregisterHotkeyAsync();
                await RegisterHotkeyAsync(Snapshot.Hotkey, capture);
                Update(s => s with { Registered = true, Status = "撮影キーを登録しました" });
            }
            else if (!capture.Recording)
            {
                await _platform.UnregisterHotkeyAsync();
                _registeredSession = null;
                Update(s => s with { Registered = false, Status = capture.Paused ? "記録の一時停止中は撮影しません" : "正式記録中に撮影できます" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Screenshot hotkey registration failed");
            Update(s => s with { Registered = false });
            await ReportFailureAsync($"撮影キーを登録できませんでした。{ex.Message} 撮影設定で競合しないキーを保存し直してください。");
        }
        finally { _registration.Release(); }
    }

    public Task CaptureAsync() => CaptureAsync(null, null);

    private Task CaptureAsync(long? expectedProfile, long? expectedSession)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_activeCapture is { IsCompleted: false })
                return ReportFailureAsync("撮影を保存中のため、今回の撮影は受け付けませんでした。完了後にもう一度押してください。");
            _activeCapture = CaptureCoreAsync(expectedProfile, expectedSession, _shutdown.Token);
            return _activeCapture;
        }
    }

    internal Task<PlayRecordingScreenshotResult> CaptureGameAsync(long expectedProfile, long expectedSession)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeCapture is { IsCompleted: false }) throw new InvalidOperationException("撮影を保存中です。完了後に再試行してください。");
            var capture = CaptureGameCoreAsync(expectedProfile, expectedSession, _shutdown.Token);
            _activeCapture = WaitForGameCaptureCompletionAsync(capture);
            return capture;
        }
    }

    private static async Task WaitForGameCaptureCompletionAsync(Task<PlayRecordingScreenshotResult> capture)
    {
        try { await capture; }
        catch (InvalidOperationException)
        {
            // The capture caller receives the failure. Shutdown only needs to wait for its completion.
        }
    }

    private async Task<PlayRecordingScreenshotResult> CaptureGameCoreAsync(long expectedProfile, long expectedSession, CancellationToken cancellationToken)
    {
        string? failure = null;
        try { return await SaveCaptureAsync(expectedProfile, expectedSession, gameOnly: true, cancellationToken); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Game screenshot capture/save failed. profile={Profile} session={Session}", expectedProfile, expectedSession);
            failure = "ゲームの画像を保存できませんでした。ゲーム画面を前面にして再試行してください。";
            throw new InvalidOperationException(failure);
        }
        finally { Update(s => s with { Busy = false, Status = failure ?? s.Status }); }
    }

    private async Task CaptureCoreAsync(long? expectedProfile, long? expectedSession, CancellationToken cancellationToken)
    {
        try
        {
            await SaveCaptureAsync(expectedProfile, expectedSession, gameOnly: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReportFailureAsync("終了待機中の撮影を中止しました。この撮影は保存されていません。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Screenshot capture/save failed");
            await ReportFailureAsync($"撮影を保存できませんでした。{ex.Message} ゲームの前面表示と撮影設定を確認して、もう一度撮影してください。");
        }
        finally
        {
            Update(s => s with { Busy = false });
        }
    }

    private async Task<PlayRecordingScreenshotResult> SaveCaptureAsync(long? expectedProfile, long? expectedSession, bool gameOnly, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Freeze the body identity before any asynchronous capture/encoding work.
        var (target, processId, playEpoch) = _capture.FreezeScreenshotPlayTarget();
        if (expectedProfile is not null && (target.CaptureGameId != expectedProfile || target.Event.SessionId != expectedSession))
            throw new InvalidOperationException("撮影対象の取得回が変わりました。");
        var capturedAt = DateTimeOffset.UtcNow;
        CaptureWindow? window;
        lock (_gate) window = _windowSession == target.Event.SessionId && _snapshot.ManualWindow ? _snapshot.Window : null;
        if (gameOnly && window?.ProcessId != processId) window = null;
        if (window is null)
        {
            var windows = _platform.ListWindows(processId).Where(x => x.ProcessId == processId).ToArray();
            if (windows.Length != 1)
                throw new InvalidOperationException(windows.Length == 0
                    ? "本文の取得先に撮影できるウィンドウがありません。撮影設定で選択してください。"
                    : "本文の取得先に複数のウィンドウがあります。撮影設定で選択してください。");
            window = windows[0];
        }
        Update(s => s with { Busy = true, Window = gameOnly ? s.Window : window, Status = "撮影を保存中" });
        var frame = gameOnly ? await _platform.CaptureGameWindowAsync(window, cancellationToken)
            : await _platform.CaptureAsync(window, cancellationToken);
        _capture.RequireScreenshotPlayTarget(playEpoch, target.CaptureGameId);
        if (gameOnly) _capture.RequireScreenshotProcessAlive(target, processId);
        await _capture.FlushPendingAsync().WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var saved = await _store.SaveScreenshotAsync(target, new(frame.Width, frame.Height, frame.PixelHash, frame.PngBytes), capturedAt, cancellationToken);
        // Publish only after commit, using the frozen event rather than the current live position.
        var success = new ScreenshotSuccess(target, saved.ImageId, _clock.GetUtcNow());
        Update(s => s with { SavedCount = s.SavedCount + 1, Status = "スクリーンショットを保存しました", LastSuccess = success });
        return new(target.Event, saved.ImageId);
    }

    internal async Task<IReadOnlyList<PlayRecordingScreenshot>> GetPlayScreenshotsAsync(long profileId, CaptureEventKey key)
    {
        try
        {
            return (await _store.GetEventScreenshotsAsync(profileId, key))
                .Select(x => new PlayRecordingScreenshot(x.ImageId, x.Width, x.Height, x.CapturedAt)).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Play screenshot metadata could not be read. profile={Profile} session={Session}", profileId, key.SessionId);
            throw new InvalidOperationException("選択した本文の画像情報を読み込めませんでした。");
        }
    }

    internal async Task<byte[]> ReadPlayScreenshotAsync(long profileId, CaptureEventKey key, string imageId)
    {
        try { return await _store.ReadEventScreenshotPngAsync(profileId, key, imageId); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Play screenshot could not be read. profile={Profile} session={Session}", profileId, key.SessionId);
            throw new InvalidOperationException("選択した本文の画像を読み込めませんでした。");
        }
    }

    private void Update(Func<ScreenshotStatus, ScreenshotStatus> update)
    {
        lock (_gate) _snapshot = update(_snapshot);
        Changed?.Invoke();
    }

    private async Task ReportFailureAsync(string message)
    {
        var now = _clock.GetUtcNow();
        bool notify;
        lock (_gate)
        {
            _snapshot = _snapshot with { Status = message, LastFailure = new(now, message) };
            notify = _lastNotificationAt is null || now - _lastNotificationAt >= TimeSpan.FromSeconds(5);
            if (notify) _lastNotificationAt = now;
        }
        Changed?.Invoke();
        if (!notify) return;
        try { _platform.SignalFailure(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Screenshot failure sound could not be played"); }
        try { await _notifications.NotifyAsync("スクリーンショットの確認が必要です", message); }
        catch (Exception ex) { _logger.LogWarning(ex, "Screenshot failure notification could not be shown"); }
    }

    private static string DescribeHotkey(ScreenshotHotkey key)
    {
        var modifiers = new List<string>();
        if ((key.Modifiers & 2) != 0) modifiers.Add("Ctrl");
        if ((key.Modifiers & 1) != 0) modifiers.Add("Alt");
        if ((key.Modifiers & 4) != 0) modifiers.Add("Shift");
        modifiers.Add(key.VirtualKey >= 0x70 ? $"F{key.VirtualKey - 0x6F}" : ((char)key.VirtualKey).ToString());
        return string.Join("+", modifiers);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            return new(_disposeTask = DisposeCoreAsync(_activeCapture));
        }
    }

    private async Task DisposeCoreAsync(Task? activeCapture)
    {
        _capture.StateChanged -= OnCaptureStateChanged;
        // Let an accepted capture save normally, then request cancellation at a safe boundary.
        _shutdown.CancelAfter(TimeSpan.FromSeconds(10));
        await _registration.WaitAsync();
        try
        {
            await _platform.UnregisterHotkeyAsync();
            Update(s => s with { Registered = false });
        }
        finally
        {
            _registration.Release();
            try { if (activeCapture is not null) await activeCapture; }
            finally { _shutdown.Dispose(); }
        }
    }
}
