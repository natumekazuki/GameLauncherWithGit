using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

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
#if WINDOWS
		return PickWindowsFilePathAsync(
			title: "実行ファイルを選択",
			filters:
			[
				new("実行ファイル (*.exe)", "*.exe"),
				new("すべてのファイル (*.*)", "*.*")
			],
			cancellationToken);
#else
		return PickFilePathAsync("実行ファイルを選択", [".exe"], cancellationToken);
#endif
	}

	public Task<string?> PickThumbnailPathAsync(CancellationToken cancellationToken = default)
	{
#if WINDOWS
		return PickWindowsFilePathAsync(
			title: "サムネイル画像を選択",
			filters:
			[
				new("画像ファイル (*.png;*.jpg;*.jpeg;*.webp)", "*.png;*.jpg;*.jpeg;*.webp"),
				new("すべてのファイル (*.*)", "*.*")
			],
			cancellationToken);
#else
		return PickFilePathAsync("サムネイル画像を選択", [".png", ".jpg", ".jpeg", ".webp"], cancellationToken);
#endif
	}

	public Task<string?> PickRepositoryDirectoryPathAsync(CancellationToken cancellationToken = default)
	{
		return PickFolderPathAsync("関連リポジトリフォルダを選択", cancellationToken);
	}

	public Task<string?> PickFolderPathAsync(string title, CancellationToken cancellationToken = default)
	{
#if WINDOWS
		var resolvedTitle = string.IsNullOrWhiteSpace(title) ? "フォルダを選択" : title.Trim();
		return PickWindowsFolderPathAsync(resolvedTitle, cancellationToken);
#else
		return Task.FromResult<string?>(null);
#endif
	}

	private async Task<string?> PickFilePathAsync(
		string pickerTitle,
		IReadOnlyList<string> allowedExtensions,
		CancellationToken cancellationToken)
	{
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
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to pick file path. title={PickerTitle}", pickerTitle);
			throw new InvalidOperationException($"ファイル参照に失敗しました。詳細: {BuildErrorDetail(ex)}", ex);
		}
	}

#if WINDOWS
	private async Task<string?> PickWindowsFilePathAsync(
		string title,
		IReadOnlyList<ComDlgFilterSpec> filters,
		CancellationToken cancellationToken)
	{
		try
		{
			return await MainThread.InvokeOnMainThreadAsync(() =>
			{
				var hwnd = GetWindowHandle();
				if (hwnd == IntPtr.Zero)
				{
					throw new InvalidOperationException("有効なウィンドウハンドルを取得できませんでした。");
				}

				return ShowFileOpenDialog(
					hwnd,
					title,
					filters,
					FileOpenOptions.ForceFileSystem | FileOpenOptions.PathMustExist | FileOpenOptions.FileMustExist | FileOpenOptions.DontAddToRecent);
			});
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to pick executable/image path. title={PickerTitle}", title);
			throw new InvalidOperationException($"ファイル参照に失敗しました。詳細: {BuildErrorDetail(ex)}", ex);
		}
		finally
		{
			cancellationToken.ThrowIfCancellationRequested();
		}
	}

	private async Task<string?> PickWindowsFolderPathAsync(string title, CancellationToken cancellationToken)
	{
		try
		{
			return await MainThread.InvokeOnMainThreadAsync(() =>
			{
				var hwnd = GetWindowHandle();
				if (hwnd == IntPtr.Zero)
				{
					throw new InvalidOperationException("有効なウィンドウハンドルを取得できませんでした。");
				}

				return ShowFileOpenDialog(
					hwnd,
					title,
					filters: [],
					FileOpenOptions.PickFolders | FileOpenOptions.ForceFileSystem | FileOpenOptions.PathMustExist | FileOpenOptions.DontAddToRecent);
			});
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to pick repository directory path.");
			throw new InvalidOperationException($"関連リポジトリフォルダの参照に失敗しました。詳細: {BuildErrorDetail(ex)}", ex);
		}
		finally
		{
			cancellationToken.ThrowIfCancellationRequested();
		}
	}

	private static string? ShowFileOpenDialog(
		IntPtr hwnd,
		string title,
		IReadOnlyList<ComDlgFilterSpec> filters,
		FileOpenOptions options)
	{
		var dialog = (IFileOpenDialog)new FileOpenDialog();
		try
		{
			dialog.SetTitle(title);
			dialog.SetOptions(options);

			if (filters.Count > 0)
			{
				dialog.SetFileTypes((uint)filters.Count, filters.ToArray());
				dialog.SetFileTypeIndex(1);
			}

			var hr = dialog.Show(hwnd);
			if (hr == HRESULT_CANCELLED)
			{
				return null;
			}

			Marshal.ThrowExceptionForHR(hr);

			dialog.GetResult(out var item);
			try
			{
				item.GetDisplayName(SIGDN_FILESYSPATH, out var filePathPointer);
				try
				{
					return Marshal.PtrToStringUni(filePathPointer);
				}
				finally
				{
					Marshal.FreeCoTaskMem(filePathPointer);
				}
			}
			finally
			{
				Marshal.ReleaseComObject(item);
			}
		}
		finally
		{
			Marshal.ReleaseComObject(dialog);
		}
	}

	private static IntPtr GetWindowHandle()
	{
		var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
		if (mauiWindow?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
		{
			var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
			if (handle != IntPtr.Zero)
			{
				return handle;
			}
		}

		var processWindowHandle = Process.GetCurrentProcess().MainWindowHandle;
		if (processWindowHandle != IntPtr.Zero)
		{
			return processWindowHandle;
		}

		var activeWindowHandle = GetActiveWindow();
		if (activeWindowHandle != IntPtr.Zero)
		{
			return activeWindowHandle;
		}

		return GetForegroundWindow();
	}

	private static string BuildErrorDetail(Exception ex)
	{
		var baseException = ex.GetBaseException();
		var baseMessage = string.IsNullOrWhiteSpace(baseException.Message) ? "(メッセージなし)" : baseException.Message;
		var hResultText = $"0x{baseException.HResult:X8}";
		return $"{baseException.GetType().Name} / HRESULT={hResultText} / {baseMessage}";
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetActiveWindow();

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);
	private const uint SIGDN_FILESYSPATH = 0x80058000;

	[Flags]
	private enum FileOpenOptions : uint
	{
		ForceFileSystem = 0x00000040,
		PathMustExist = 0x00000800,
		FileMustExist = 0x00001000,
		PickFolders = 0x00000020,
		DontAddToRecent = 0x02000000
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ComDlgFilterSpec
	{
		public ComDlgFilterSpec(string name, string spec)
		{
			Name = name;
			Spec = spec;
		}

		[MarshalAs(UnmanagedType.LPWStr)]
		public string Name;

		[MarshalAs(UnmanagedType.LPWStr)]
		public string Spec;
	}

	[ComImport]
	[Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IFileOpenDialog
	{
		[PreserveSig]
		int Show(IntPtr parent);

		void SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] ComDlgFilterSpec[] rgFilterSpec);
		void SetFileTypeIndex(uint iFileType);
		void GetFileTypeIndex(out uint piFileType);
		void Advise(IntPtr pfde, out uint pdwCookie);
		void Unadvise(uint dwCookie);
		void SetOptions(FileOpenOptions fos);
		void GetOptions(out FileOpenOptions pfos);
		void SetDefaultFolder(IntPtr psi);
		void SetFolder(IntPtr psi);
		void GetFolder(out IntPtr ppsi);
		void GetCurrentSelection(out IntPtr ppsi);
		void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
		void GetFileName(out IntPtr pszName);
		void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
		void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
		void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
		void GetResult(out IShellItem ppsi);
		void AddPlace(IntPtr psi, uint fdap);
		void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
		void Close(int hr);
		void SetClientGuid(ref Guid guid);
		void ClearClientData();
		void SetFilter(IntPtr pFilter);
		void GetResults(out IntPtr ppenum);
		void GetSelectedItems(out IntPtr ppsai);
	}

	[ComImport]
	[Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
	private class FileOpenDialog
	{
	}

	[ComImport]
	[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItem
	{
		void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
		void GetParent(out IShellItem ppsi);
		void GetDisplayName(uint sigdnName, out IntPtr ppszName);
		void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
		void Compare(IShellItem psi, uint hint, out int piOrder);
	}
#endif
}
