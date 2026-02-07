using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Domain.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class TrayService : ITrayService, IDisposable
{
	private readonly ILogger<TrayService> _logger;
#if WINDOWS
	private static readonly ConcurrentDictionary<nint, TrayService> HookedWindows = new();
	private static readonly WindowProcDelegate WindowProcHandler = WindowProcedure;

	private readonly object _gate = new();
	private readonly IGameLibraryStore _gameLibraryStore;
	private readonly ILogAccessService _logAccessService;
	private readonly INotificationService _notificationService;
	private readonly ISettingsPanelService _settingsPanelService;
	private readonly IServiceProvider _serviceProvider;

	private RepositorySyncState _currentState = RepositorySyncState.Idle;
	private bool _isIconAdded;
	private bool _isDisposed;
	private bool _allowWindowClose;
	private IntPtr _windowHandle;
	private IntPtr _originalWndProc;
#endif

	public TrayService(
		ILogger<TrayService> logger
#if WINDOWS
		,
		IGameLibraryStore gameLibraryStore,
		ILogAccessService logAccessService,
		INotificationService notificationService,
		ISettingsPanelService settingsPanelService,
		IServiceProvider serviceProvider
#endif
		)
	{
		_logger = logger;
#if WINDOWS
		_gameLibraryStore = gameLibraryStore;
		_logAccessService = logAccessService;
		_notificationService = notificationService;
		_settingsPanelService = settingsPanelService;
		_serviceProvider = serviceProvider;
#endif
	}

	public void SetState(RepositorySyncState state)
	{
#if WINDOWS
		MainThread.BeginInvokeOnMainThread(() =>
		{
			lock (_gate)
			{
				if (_isDisposed)
				{
					return;
				}

				EnsureIconAdded();
				ApplyState(state);
			}
		});
#else
		_logger.LogInformation("Tray state changed. state={State}", state);
#endif
	}

	public void Dispose()
	{
#if WINDOWS
		MainThread.BeginInvokeOnMainThread(() =>
		{
			lock (_gate)
			{
				if (_isDisposed)
				{
					return;
				}

				TryDeleteIcon();
				UnhookWindowProcedure();
				_isDisposed = true;
			}
		});
#endif
	}

#if WINDOWS
	private void EnsureIconAdded()
	{
		var hwnd = GetWindowHandle();
		if (hwnd == IntPtr.Zero)
		{
			_logger.LogWarning("Tray icon add skipped. Window handle was not found.");
			return;
		}

		if (_windowHandle != hwnd)
		{
			UnhookWindowProcedure();
			_windowHandle = hwnd;
		}

		HookWindowProcedure();
		if (_isIconAdded)
		{
			return;
		}

		var data = CreateNotifyIconData(hwnd, _currentState);
		var added = Shell_NotifyIcon(NIM_ADD, ref data);
		if (!added)
		{
			_logger.LogWarning("Shell_NotifyIcon(NIM_ADD) failed.");
			return;
		}

		data.uVersion = NOTIFYICON_VERSION_4;
		_ = Shell_NotifyIcon(NIM_SETVERSION, ref data);
		_isIconAdded = true;
	}

	private void HookWindowProcedure()
	{
		if (_windowHandle == IntPtr.Zero || _originalWndProc != IntPtr.Zero)
		{
			return;
		}

		SetLastError(0);
		var newProc = Marshal.GetFunctionPointerForDelegate(WindowProcHandler);
		var previousProc = SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, newProc);
		var lastError = Marshal.GetLastWin32Error();

		if (previousProc == IntPtr.Zero && lastError != 0)
		{
			_logger.LogWarning(
				"SetWindowLongPtr(GWLP_WNDPROC) failed. hwnd={Handle}, error={Error}",
				_windowHandle,
				lastError);
			return;
		}

		_originalWndProc = previousProc;
		HookedWindows[_windowHandle] = this;
	}

	private void UnhookWindowProcedure()
	{
		if (_windowHandle == IntPtr.Zero)
		{
			return;
		}

		if (_originalWndProc != IntPtr.Zero)
		{
			_ = SetWindowLongPtr(_windowHandle, GWLP_WNDPROC, _originalWndProc);
		}

		HookedWindows.TryRemove(_windowHandle, out _);
		_originalWndProc = IntPtr.Zero;
	}

	private IntPtr HandleWindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
	{
		switch (message)
		{
			case WM_TRAY_CALLBACK:
			{
				var eventId = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
				if (eventId is WM_RBUTTONUP or WM_CONTEXTMENU)
				{
					ShowContextMenu(hwnd);
					return IntPtr.Zero;
				}

				if (eventId is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
				{
					ShowMainWindow(hwnd);
					return IntPtr.Zero;
				}

				break;
			}
			case WM_COMMAND:
			{
				var commandId = unchecked((uint)wParam.ToInt64()) & 0xFFFF;
				if (HandleMenuCommand(hwnd, commandId))
				{
					return IntPtr.Zero;
				}

				break;
			}
			case WM_CLOSE:
			{
				if (!_allowWindowClose)
				{
					ShowWindow(hwnd, SW_HIDE);
					return IntPtr.Zero;
				}

				break;
			}
		}

		return _originalWndProc != IntPtr.Zero
			? CallWindowProc(_originalWndProc, hwnd, message, wParam, lParam)
			: DefWindowProc(hwnd, message, wParam, lParam);
	}

	private bool HandleMenuCommand(IntPtr hwnd, uint commandId)
	{
		switch (commandId)
		{
			case MenuCommandSyncNow:
				_ = ExecuteSyncNowAsync();
				return true;
			case MenuCommandOpenSettings:
				_ = ExecuteOpenSettingsAsync(hwnd);
				return true;
			case MenuCommandOpenLog:
				_ = ExecuteOpenLogAsync();
				return true;
			case MenuCommandExit:
				ExecuteExit(hwnd);
				return true;
			default:
				return false;
		}
	}

	private async Task ExecuteSyncNowAsync()
	{
		try
		{
			var syncOrchestrator = _serviceProvider.GetRequiredService<ISyncOrchestrator>();
			var games = await _gameLibraryStore.GetAllAsync();
			var repositories = games
				.Select(static game => NormalizeRepositoryPath(game.RelatedRepositoryPath))
				.Where(static path => path is not null)
				.Select(static path => path!)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();

			if (repositories.Length == 0)
			{
				await _notificationService.NotifyAsync(
					"同期対象がありません",
					"関連リポジトリが未設定のため同期は実行されませんでした。");
				return;
			}

			foreach (var repository in repositories)
			{
				await syncOrchestrator.QueueRepositorySyncAsync(repository);
			}

			await _notificationService.NotifyAsync(
				"同期を開始しました",
				$"{repositories.Length}件のリポジトリを同期キューへ追加しました。");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Tray menu command failed. command=sync-now");
			await AppendTrayErrorAsync($"トレイメニューの「今すぐ同期」に失敗しました。{ex.Message}");
		}
	}

	private Task ExecuteOpenSettingsAsync(IntPtr hwnd)
	{
		try
		{
			ShowMainWindow(hwnd);
			_settingsPanelService.RequestOpen();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Tray menu command failed. command=open-settings");
		}

		return Task.CompletedTask;
	}

	private async Task ExecuteOpenLogAsync()
	{
		try
		{
			await _logAccessService.OpenLatestErrorLogAsync();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Tray menu command failed. command=open-log");
			await AppendTrayErrorAsync($"トレイメニューの「ログを開く」に失敗しました。{ex.Message}");
		}
	}

	private void ExecuteExit(IntPtr hwnd)
	{
		_allowWindowClose = true;
		ShowWindow(hwnd, SW_RESTORE);
		_ = PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
	}

	private async Task AppendTrayErrorAsync(string message)
	{
		try
		{
			await _logAccessService.AppendErrorAsync(message);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to append tray error log.");
		}
	}

	private static string? NormalizeRepositoryPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		try
		{
			var normalized = Path.GetFullPath(path.Trim());
			return Directory.Exists(normalized) ? normalized : null;
		}
		catch
		{
			return null;
		}
	}

	private void ShowContextMenu(IntPtr hwnd)
	{
		var menu = CreatePopupMenu();
		if (menu == IntPtr.Zero)
		{
			return;
		}

		try
		{
			_ = AppendMenu(menu, MF_STRING, MenuCommandSyncNow, "今すぐ同期");
			_ = AppendMenu(menu, MF_STRING, MenuCommandOpenSettings, "設定");
			_ = AppendMenu(menu, MF_STRING, MenuCommandOpenLog, "ログを開く");
			_ = AppendMenu(menu, MF_SEPARATOR, 0, string.Empty);
			_ = AppendMenu(menu, MF_STRING, MenuCommandExit, "終了");

			if (!GetCursorPos(out var cursorPosition))
			{
				return;
			}

			_ = SetForegroundWindow(hwnd);
			_ = TrackPopupMenu(
				menu,
				TPM_LEFTALIGN | TPM_RIGHTBUTTON,
				cursorPosition.X,
				cursorPosition.Y,
				0,
				hwnd,
				IntPtr.Zero);
			_ = PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
		}
		finally
		{
			_ = DestroyMenu(menu);
		}
	}

	private void ShowMainWindow(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			hwnd = GetWindowHandle();
			if (hwnd == IntPtr.Zero)
			{
				return;
			}
		}

		_ = ShowWindow(hwnd, SW_RESTORE);
		_ = SetForegroundWindow(hwnd);
	}

	private void ApplyState(RepositorySyncState state)
	{
		if (!_isIconAdded || _currentState == state)
		{
			return;
		}

		_currentState = state;
		var hwnd = _windowHandle != IntPtr.Zero ? _windowHandle : GetWindowHandle();
		if (hwnd == IntPtr.Zero)
		{
			_logger.LogWarning("Tray icon modify skipped. Window handle was not found.");
			return;
		}

		var data = CreateNotifyIconData(hwnd, state);
		var modified = Shell_NotifyIcon(NIM_MODIFY, ref data);
		if (!modified)
		{
			_logger.LogWarning("Shell_NotifyIcon(NIM_MODIFY) failed. state={State}", state);
			return;
		}

		if (state == RepositorySyncState.ErrorPaused)
		{
			ShowErrorBalloon(hwnd);
		}
	}

	private void ShowErrorBalloon(IntPtr hwnd)
	{
		var data = CreateNotifyIconData(hwnd, RepositorySyncState.ErrorPaused);
		data.uFlags = NIF_INFO;
		data.szInfo = "同期エラーにより自動同期を停止しました。ログを確認してください。";
		data.szInfoTitle = "GameLauncherWithGit";
		data.dwInfoFlags = NIIF_ERROR;
		_ = Shell_NotifyIcon(NIM_MODIFY, ref data);
	}

	private void TryDeleteIcon()
	{
		if (!_isIconAdded || _windowHandle == IntPtr.Zero)
		{
			_isIconAdded = false;
			return;
		}

		var data = new NotifyIconData
		{
			cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
			hWnd = _windowHandle,
			uID = TrayIconId
		};

		_ = Shell_NotifyIcon(NIM_DELETE, ref data);
		_isIconAdded = false;
	}

	private static NotifyIconData CreateNotifyIconData(IntPtr hwnd, RepositorySyncState state)
	{
		return new NotifyIconData
		{
			cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
			hWnd = hwnd,
			uID = TrayIconId,
			uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
			uCallbackMessage = WM_TRAY_CALLBACK,
			hIcon = SelectIconHandle(state),
			szTip = BuildTooltip(state),
			szInfo = string.Empty,
			szInfoTitle = string.Empty,
			uVersion = NOTIFYICON_VERSION_4
		};
	}

	private static string BuildTooltip(RepositorySyncState state)
	{
		var suffix = state switch
		{
			RepositorySyncState.Syncing or RepositorySyncState.Debouncing => "同期中",
			RepositorySyncState.ErrorPaused => "エラー停止",
			_ => "待機中"
		};
		return $"GameLauncherWithGit - {suffix}";
	}

	private static IntPtr SelectIconHandle(RepositorySyncState state)
	{
		return state switch
		{
			RepositorySyncState.Syncing or RepositorySyncState.Debouncing => LoadIcon(IntPtr.Zero, IDI_INFORMATION),
			RepositorySyncState.ErrorPaused => LoadIcon(IntPtr.Zero, IDI_ERROR),
			_ => LoadIcon(IntPtr.Zero, IDI_APPLICATION)
		};
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

		var processHandle = Process.GetCurrentProcess().MainWindowHandle;
		if (processHandle != IntPtr.Zero)
		{
			return processHandle;
		}

		return GetForegroundWindow();
	}

	private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
	{
		if (HookedWindows.TryGetValue(hwnd, out var service))
		{
			return service.HandleWindowMessage(hwnd, message, wParam, lParam);
		}

		return DefWindowProc(hwnd, message, wParam, lParam);
	}

	private delegate IntPtr WindowProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

	private const uint NIM_ADD = 0x00000000;
	private const uint NIM_MODIFY = 0x00000001;
	private const uint NIM_DELETE = 0x00000002;
	private const uint NIM_SETVERSION = 0x00000004;
	private const uint NIF_MESSAGE = 0x00000001;
	private const uint NIF_ICON = 0x00000002;
	private const uint NIF_TIP = 0x00000004;
	private const uint NIF_INFO = 0x00000010;
	private const uint NIIF_ERROR = 0x00000003;
	private const uint NOTIFYICON_VERSION_4 = 4;
	private const uint WM_APP = 0x8000;
	private const uint WM_TRAY_CALLBACK = WM_APP + 1;
	private const uint WM_COMMAND = 0x0111;
	private const uint WM_CONTEXTMENU = 0x007B;
	private const uint WM_RBUTTONUP = 0x0205;
	private const uint WM_LBUTTONUP = 0x0202;
	private const uint WM_LBUTTONDBLCLK = 0x0203;
	private const uint WM_NULL = 0x0000;
	private const uint WM_CLOSE = 0x0010;
	private const uint TrayIconId = 1001;
	private const uint MenuCommandSyncNow = 1101;
	private const uint MenuCommandOpenSettings = 1102;
	private const uint MenuCommandOpenLog = 1103;
	private const uint MenuCommandExit = 1199;
	private const uint MF_STRING = 0x00000000;
	private const uint MF_SEPARATOR = 0x00000800;
	private const uint TPM_LEFTALIGN = 0x0000;
	private const uint TPM_RIGHTBUTTON = 0x0002;
	private const int SW_HIDE = 0;
	private const int SW_RESTORE = 9;
	private const int IDI_APPLICATION = 32512;
	private const int IDI_ERROR = 32513;
	private const int IDI_INFORMATION = 32516;
	private const int GWLP_WNDPROC = -4;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NotifyIconData
	{
		public uint cbSize;
		public IntPtr hWnd;
		public uint uID;
		public uint uFlags;
		public uint uCallbackMessage;
		public IntPtr hIcon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string szTip;
		public uint dwState;
		public uint dwStateMask;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string szInfo;
		public uint uVersion;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string szInfoTitle;
		public uint dwInfoFlags;
		public Guid guidItem;
		public IntPtr hBalloonIcon;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Point
	{
		public int X;
		public int Y;
	}

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr LoadIcon(IntPtr hInstance, int lpIconName);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr CreatePopupMenu();

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyMenu(IntPtr hMenu);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetCursorPos(out Point lpPoint);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool TrackPopupMenu(
		IntPtr hMenu,
		uint uFlags,
		int x,
		int y,
		int nReserved,
		IntPtr hWnd,
		IntPtr prcRect);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
	private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

	[DllImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll")]
	private static extern void SetLastError(uint dwErrCode);

	private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
	{
		return IntPtr.Size == 8
			? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
			: new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
	}
#endif
}
