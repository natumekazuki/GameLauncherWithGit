using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Abstractions;
#if WINDOWS
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
#endif

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class ScenarioScreenshotPlatform : IScenarioScreenshotPlatform
{
#if WINDOWS
    private const nuint SubclassId = 0x534352;
    private readonly SubclassProc _windowProc;
    private nint _hotkeyWindow;
    private int _nextHotkeyId;
    private int _hotkeyId;
    private Action? _hotkeyCallback;
    private bool _subclassInstalled;
    private bool _disposed;

    public ScenarioScreenshotPlatform() => _windowProc = WindowProcedure;
#endif

    public IReadOnlyList<CaptureWindow> ListWindows(int? processId = null)
    {
#if WINDOWS
        ObjectDisposedException.ThrowIf(_disposed, this);
        var windows = new List<CaptureWindow>();
        if (!EnumWindows((hwnd, _) =>
        {
            if (!IsCaptureCandidate(hwnd) || GetWindow(hwnd, 4) != 0 || (GetWindowLong(hwnd, -20) & 0x80) != 0)
                return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId || (processId.HasValue && processId != pid))
                return true;
            var title = new StringBuilder(GetWindowTextLength(hwnd) + 1);
            GetWindowText(hwnd, title, title.Capacity);
            try
            {
                using var process = Process.GetProcessById(pid);
                windows.Add(new(hwnd, pid, process.ProcessName, title.ToString()));
            }
            catch (ArgumentException) { } // Enumeration can race process exit.
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            return true;
        }, 0))
            throw NativeError("ウィンドウ一覧を取得できませんでした。");
        return windows;
#else
        throw Unsupported();
#endif
    }

    public Task<ScenarioScreenshotFrame> CaptureAsync(CaptureWindow target, CancellationToken cancellationToken = default)
    {
#if WINDOWS
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(target);
        if (!GetClientRect(target.Handle, out var rect)) throw NativeError("クライアント領域を取得できませんでした。");
        var origin = new Point();
        if (!ClientToScreen(target.Handle, ref origin)) throw NativeError("クライアント領域の画面座標を取得できませんでした。");
        var width = checked(rect.Right - rect.Left);
        var height = checked(rect.Bottom - rect.Top);
        var length = checked(checked(width * 4) * height);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("撮影対象に有効なクライアント領域がありません。");

        nint screen = 0, memory = 0, bitmap = 0, previous = 0;
        var bgra = new byte[length];
        try
        {
            screen = GetDC(0);
            if (screen == 0) throw NativeError("画面の取得に失敗しました。");
            memory = CreateCompatibleDC(screen);
            if (memory == 0) throw NativeError("撮影用DCを作成できませんでした。");
            bitmap = CreateCompatibleBitmap(screen, width, height);
            if (bitmap == 0) throw NativeError("撮影用ビットマップを作成できませんでした。");
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) throw NativeError("撮影用ビットマップを選択できませんでした。");
            ValidateTarget(target);
            if (!BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020))
                throw NativeError("画面の撮影に失敗しました。");
            // GetDIBits requires the bitmap not to be selected into a DC.
            var restored = SelectObject(memory, previous);
            if (restored == 0 || restored == -1) throw NativeError("撮影用ビットマップを解除できませんでした。");
            previous = 0;
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
                    Planes = 1, BitCount = 32, SizeImage = (uint)length
                }
            };
            if (GetDIBits(screen, bitmap, 0, (uint)height, bgra, ref info, 0) != height)
                throw NativeError("撮影した画素を読み取れませんでした。");
        }
        finally
        {
            if (previous != 0 && previous != -1 && memory != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }

        // Freeze desktop pixels before yielding; PNG encoding and hashing must not delay capture.
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rgba = ScenarioScreenshotPixels.NormalizeBgra32(width, height, bgra, checked(width * 4), false);
            using var pixels = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            if (pixels.GetPixels() == 0) throw new InvalidOperationException("PNG変換用の画素を確保できませんでした。");
            Marshal.Copy(rgba, 0, pixels.GetPixels(), rgba.Length);
            using var image = SKImage.FromBitmap(pixels);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("PNGへの変換に失敗しました。");
            cancellationToken.ThrowIfCancellationRequested();
            return new ScenarioScreenshotFrame(width, height, encoded.ToArray(), ScenarioScreenshotPixels.ComputeHash(width, height, rgba));
        }, cancellationToken);
#else
        throw Unsupported();
#endif
    }

    public Task RegisterHotkeyAsync(ScreenshotHotkey hotkey, Action callback)
    {
#if WINDOWS
        ArgumentNullException.ThrowIfNull(hotkey);
        ArgumentNullException.ThrowIfNull(callback);
        var supportedKey = hotkey.VirtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7A;
        if (!supportedKey || hotkey.Modifiers == 0 || (hotkey.Modifiers & ~7u) != 0)
            throw new ArgumentException("Ctrl/Alt/Shiftと英数字またはF1～F11を指定してください。", nameof(hotkey));
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            UnregisterOnMainThread();
            if (_nextHotkeyId == 0xBFFF) throw new InvalidOperationException("ホットキー登録IDを使い切りました。");
            var nativeWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window
                ?? throw new InvalidOperationException("アプリのウィンドウが初期化されていません。");
            _hotkeyWindow = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
            if (_hotkeyWindow == 0) throw new InvalidOperationException("アプリのウィンドウが初期化されていません。");
            if (!SetWindowSubclass(_hotkeyWindow, _windowProc, SubclassId, 0))
                throw NativeError("ホットキーの受信処理を登録できませんでした。");
            _subclassInstalled = true;
            var id = ++_nextHotkeyId;
            if (!RegisterHotKey(_hotkeyWindow, id, hotkey.Modifiers | 0x4000, hotkey.VirtualKey))
            {
                var error = NativeError("ホットキーを登録できませんでした。他のアプリとの競合を確認してください。");
                UnregisterOnMainThread();
                throw error;
            }
            _hotkeyId = id;
            _hotkeyCallback = callback;
        });
#else
        throw Unsupported();
#endif
    }

    public Task UnregisterHotkeyAsync()
    {
#if WINDOWS
        return MainThread.InvokeOnMainThreadAsync(UnregisterOnMainThread);
#else
        throw Unsupported();
#endif
    }

    public void SignalFailure()
    {
#if WINDOWS
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!MessageBeep(0x00000030)) // MB_ICONEXCLAMATION
            throw NativeError("撮影失敗の通知音を再生できませんでした。");
#else
        throw Unsupported();
#endif
    }

    public void Dispose()
    {
#if WINDOWS
        // UnregisterHotKey and window subclass operations must use the registering UI thread.
        if (MainThread.IsMainThread) DisposeOnMainThread();
        else MainThread.InvokeOnMainThreadAsync(DisposeOnMainThread).GetAwaiter().GetResult();
#endif
    }

#if WINDOWS
    private void DisposeOnMainThread()
    {
        if (_disposed) return;
        UnregisterOnMainThread();
        _disposed = true;
    }

    private void UnregisterOnMainThread()
    {
        if (_hotkeyId != 0)
        {
            if (!UnregisterHotKey(_hotkeyWindow, _hotkeyId)) throw NativeError("ホットキーを解除できませんでした。");
            _hotkeyId = 0;
            _hotkeyCallback = null;
        }
        if (_subclassInstalled)
        {
            if (!RemoveWindowSubclass(_hotkeyWindow, _windowProc, SubclassId)) throw NativeError("ホットキーの受信処理を解除できませんでした。");
            _subclassInstalled = false;
        }
        _hotkeyWindow = 0;
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint data)
    {
        if (message == 0x0312 && _hotkeyId != 0 && wParam == _hotkeyId)
        {
            // Do not let managed exceptions cross the native callback boundary.
            try { _hotkeyCallback?.Invoke(); }
            catch (Exception ex) { Trace.TraceError("Screenshot hotkey callback failed: {0}", ex); }
            return 0;
        }
        if (message == 0x0082) // WM_NCDESTROY
        {
            if (_hotkeyId != 0) UnregisterHotKey(hwnd, _hotkeyId);
            RemoveWindowSubclass(hwnd, _windowProc, SubclassId);
            _hotkeyId = 0;
            _hotkeyCallback = null;
            _subclassInstalled = false;
            _hotkeyWindow = 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private static bool IsCaptureCandidate(nint hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd)
        && DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked == 0
        && GetClientRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;

    private static void ValidateTarget(CaptureWindow target)
    {
        GetWindowThreadProcessId(target.Handle, out var pid);
        if (target.Handle == 0 || pid != target.ProcessId || !IsCaptureCandidate(target.Handle))
            throw new InvalidOperationException("撮影対象のウィンドウが終了または変更されました。選び直してください。");
        if (GetForegroundWindow() != target.Handle)
            throw new InvalidOperationException("撮影対象のゲームを最前面にしてください。");
    }

    private static InvalidOperationException NativeError(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));
    private delegate bool EnumWindowsProc(nint hwnd, nint data);
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc callback, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool MessageBeep(uint type);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfo info, uint usage);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("comctl32.dll", ExactSpelling = true, SetLastError = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll", ExactSpelling = true, SetLastError = true)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll", ExactSpelling = true)] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
#else
    private static PlatformNotSupportedException Unsupported() => new("スクリーンショット撮影とグローバルホットキーはWindowsでのみ利用できます。");
#endif
}
