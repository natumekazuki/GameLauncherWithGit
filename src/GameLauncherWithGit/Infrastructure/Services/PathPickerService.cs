using System.Runtime.InteropServices;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

#if WINDOWS
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
		return PickFilePathAsync("実行ファイルを選択", [".exe"], cancellationToken);
	}

	public Task<string?> PickThumbnailPathAsync(CancellationToken cancellationToken = default)
	{
		return PickFilePathAsync("サムネイル画像を選択", [".png", ".jpg", ".jpeg", ".webp"], cancellationToken);
	}

	public async Task<string?> PickRepositoryDirectoryPathAsync(CancellationToken cancellationToken = default)
	{
#if WINDOWS
		try
		{
			return await MainThread.InvokeOnMainThreadAsync(async () =>
			{
				var windowHandle = GetWindowHandle();
				if (windowHandle == IntPtr.Zero)
				{
					throw new InvalidOperationException("ウィンドウハンドルを取得できませんでした。");
				}

				var folderPicker = new FolderPicker
				{
					SuggestedStartLocation = PickerLocationId.DocumentsLibrary
				};
				folderPicker.FileTypeFilter.Add("*");
				InitializeWithWindow.Initialize(folderPicker, windowHandle);

				var folder = await folderPicker.PickSingleFolderAsync();
				cancellationToken.ThrowIfCancellationRequested();
				return folder?.Path;
			});
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to pick repository directory path.");
			throw new InvalidOperationException("関連リポジトリフォルダの参照に失敗しました。", ex);
		}
#else
		await Task.CompletedTask;
		_logger.LogWarning("Repository directory picker is not supported on this platform.");
		return null;
#endif
	}

	private async Task<string?> PickFilePathAsync(
		string pickerTitle,
		IReadOnlyList<string> allowedExtensions,
		CancellationToken cancellationToken)
	{
#if WINDOWS
		try
		{
			return await MainThread.InvokeOnMainThreadAsync(async () =>
			{
				var windowHandle = GetWindowHandle();
				if (windowHandle == IntPtr.Zero)
				{
					throw new InvalidOperationException("ウィンドウハンドルを取得できませんでした。");
				}

				var filePicker = new FileOpenPicker
				{
					SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
					ViewMode = PickerViewMode.List
				};

				foreach (var extension in allowedExtensions)
				{
					filePicker.FileTypeFilter.Add(extension);
				}

				InitializeWithWindow.Initialize(filePicker, windowHandle);
				var file = await filePicker.PickSingleFileAsync();
				cancellationToken.ThrowIfCancellationRequested();
				return file?.Path;
			});
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to pick file path. title={PickerTitle}", pickerTitle);
			throw new InvalidOperationException("ファイル参照に失敗しました。", ex);
		}
#else
		try
		{
			var options = new PickOptions
			{
				PickerTitle = pickerTitle,
				FileTypes = new FilePickerFileType(
					new Dictionary<DevicePlatform, IEnumerable<string>>
					{
						[DevicePlatform.WinUI] = allowedExtensions
					})
			};

			var result = await FilePicker.Default.PickAsync(options);
			cancellationToken.ThrowIfCancellationRequested();
			return result?.FullPath;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
#endif
	}

#if WINDOWS
	private static IntPtr GetWindowHandle()
	{
		var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
		if (mauiWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
		{
			return WindowNative.GetWindowHandle(nativeWindow);
		}

		var activeWindowHandle = GetActiveWindow();
		if (activeWindowHandle != IntPtr.Zero)
		{
			return activeWindowHandle;
		}

		return GetForegroundWindow();
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetActiveWindow();

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();
#endif
}
