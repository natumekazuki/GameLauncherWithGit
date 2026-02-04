using GameLauncherWithGit.App.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using WinRT.Interop;
using Windows.Storage.Pickers;

namespace GameLauncherWithGit.App.Infrastructure.Services;

public sealed class PathPickerService : IPathPickerService
{
    private readonly ILogger<PathPickerService> _logger;

    public PathPickerService(ILogger<PathPickerService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> PickExecutablePathAsync(CancellationToken cancellationToken = default)
    {
        var options = new PickOptions
        {
            PickerTitle = "実行ファイルを選択",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = new[] { ".exe" },
            }),
        };

        return await PickFilePathAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> PickThumbnailImagePathAsync(CancellationToken cancellationToken = default)
    {
        var options = new PickOptions
        {
            PickerTitle = "サムネイル画像を選択",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" },
            }),
        };

        return await PickFilePathAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> PickRepositoryFolderPathAsync(CancellationToken cancellationToken = default)
    {
        string? selectedPath = null;

        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            IntPtr hwnd = ResolveMainWindowHandle();
            if (hwnd == IntPtr.Zero)
            {
                _logger.LogWarning("フォルダピッカーを開けません: メインウィンドウハンドル未取得");
                return;
            }

            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add("*");

            InitializeWithWindow.Initialize(picker, hwnd);

            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            selectedPath = folder?.Path;
        });

        return selectedPath;
    }

    private static async Task<string?> PickFilePathAsync(PickOptions options, CancellationToken cancellationToken)
    {
        FileResult? result = null;

        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await FilePicker.Default.PickAsync(options);
        });

        return result?.FullPath;
    }

    private static IntPtr ResolveMainWindowHandle()
    {
        Window? window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
        if (window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
        {
            return IntPtr.Zero;
        }

        return WindowNative.GetWindowHandle(nativeWindow);
    }
}
