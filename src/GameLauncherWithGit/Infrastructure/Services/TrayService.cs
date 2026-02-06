using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLauncherWithGit.Domain.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class TrayService : ITrayService, IDisposable
{
	private readonly ILogger<TrayService> _logger;
#if WINDOWS
	private readonly object _gate = new();
	private RepositorySyncState _currentState = RepositorySyncState.Idle;
	private bool _isIconAdded;
	private bool _isDisposed;
#endif

	public TrayService(ILogger<TrayService> logger)
	{
		_logger = logger;
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
				_isDisposed = true;
			}
		});
#endif
	}

#if WINDOWS
	private void EnsureIconAdded()
	{
		if (_isIconAdded)
		{
			return;
		}

		var hwnd = GetWindowHandle();
		if (hwnd == IntPtr.Zero)
		{
			_logger.LogWarning("Tray icon add skipped. Window handle was not found.");
			return;
		}

		var data = CreateNotifyIconData(hwnd, _currentState);
		var added = Shell_NotifyIcon(NIM_ADD, ref data);
		if (!added)
		{
			_logger.LogWarning("Shell_NotifyIcon(NIM_ADD) failed.");
			return;
		}

		_isIconAdded = true;
	}

	private void ApplyState(RepositorySyncState state)
	{
		if (!_isIconAdded || _currentState == state)
		{
			return;
		}

		_currentState = state;

		var hwnd = GetWindowHandle();
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
		if (!_isIconAdded)
		{
			return;
		}

		var hwnd = GetWindowHandle();
		if (hwnd == IntPtr.Zero)
		{
			_isIconAdded = false;
			return;
		}

		var data = new NotifyIconData
		{
			cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
			hWnd = hwnd,
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
			uFlags = NIF_ICON | NIF_TIP,
			uCallbackMessage = WM_APP + 1,
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

	private const uint NIM_ADD = 0x00000000;
	private const uint NIM_MODIFY = 0x00000001;
	private const uint NIM_DELETE = 0x00000002;
	private const uint NIF_ICON = 0x00000002;
	private const uint NIF_TIP = 0x00000004;
	private const uint NIF_INFO = 0x00000010;
	private const uint NIIF_ERROR = 0x00000003;
	private const uint NOTIFYICON_VERSION_4 = 4;
	private const uint WM_APP = 0x8000;
	private const uint TrayIconId = 1001;
	private const int IDI_APPLICATION = 32512;
	private const int IDI_ERROR = 32513;
	private const int IDI_INFORMATION = 32516;

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

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr LoadIcon(IntPtr hInstance, int lpIconName);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();
#endif
}
