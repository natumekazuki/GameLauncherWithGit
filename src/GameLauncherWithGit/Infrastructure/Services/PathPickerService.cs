using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

#if WINDOWS
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;
#endif

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class PathPickerService : IPathPickerService
{
	private readonly ILogger<PathPickerService> _logger;

	public PathPickerService(ILogger<PathPickerService> logger)
	{
		_logger = logger;
	}

	public Task<string?> PickExecutablePathAsync(CancellationToken cancellationToken = default)
	{
		var options = new PickOptions
		{
			PickerTitle = "実行ファイルを選択",
			FileTypes = new FilePickerFileType(
				new Dictionary<DevicePlatform, IEnumerable<string>>
				{
					[DevicePlatform.WinUI] = [".exe"]
				})
		};

		return PickFilePathAsync(options, cancellationToken);
	}

	public async Task<string?> PickRepositoryDirectoryPathAsync(CancellationToken cancellationToken = default)
	{
#if WINDOWS
		try
		{
			var folderPicker = new FolderPicker
			{
				SuggestedStartLocation = PickerLocationId.DocumentsLibrary
			};
			folderPicker.FileTypeFilter.Add("*");

			var windowHandle = GetWindowHandle();
			if (windowHandle == IntPtr.Zero)
			{
				_logger.LogWarning("Window handle could not be resolved for repository folder picker.");
				return null;
			}

			InitializeWithWindow.Initialize(folderPicker, windowHandle);
			var folder = await folderPicker.PickSingleFolderAsync();
			return folder?.Path;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to pick repository directory path.");
			return null;
		}
#else
		await Task.CompletedTask;
		_logger.LogWarning("Repository directory picker is not supported on this platform.");
		return null;
#endif
	}

	public Task<string?> PickThumbnailPathAsync(CancellationToken cancellationToken = default)
	{
		var options = new PickOptions
		{
			PickerTitle = "サムネイル画像を選択",
			FileTypes = new FilePickerFileType(
				new Dictionary<DevicePlatform, IEnumerable<string>>
				{
					[DevicePlatform.WinUI] = [".png", ".jpg", ".jpeg", ".webp"]
				})
		};

		return PickFilePathAsync(options, cancellationToken);
	}

	private async Task<string?> PickFilePathAsync(PickOptions options, CancellationToken cancellationToken)
	{
		try
		{
			var result = await FilePicker.Default.PickAsync(options);
			cancellationToken.ThrowIfCancellationRequested();
			return result?.FullPath;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to pick file path.");
			return null;
		}
	}

#if WINDOWS
	private static IntPtr GetWindowHandle()
	{
		var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
		if (mauiWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
		{
			return WindowNative.GetWindowHandle(nativeWindow);
		}

		return IntPtr.Zero;
	}
#endif
}
