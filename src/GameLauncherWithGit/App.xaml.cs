using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit;

public partial class App : Microsoft.Maui.Controls.Application
{
	private const string SingleInstanceMutexName = "GameLauncherWithGit.SingleInstance";
	private const int WindowSizeSaveDelayMilliseconds = 500;
	private static Mutex? singleInstanceMutex;

	private readonly IAppSettingsService _appSettings;
	private CancellationTokenSource? _windowSizeSaveCancellation;
#if WINDOWS
	private readonly GameLauncherWithGit.Infrastructure.Mcp.ScenarioMcpHost _mcp;
	private readonly Task _mcpStartup;
	private readonly GameLauncherWithGit.Infrastructure.PlayRecordingWeb.PlayRecordingWebHost _playRecordingWeb;
#endif

	public App(IAppSettingsService appSettings, GameLauncherWithGit.Application.Services.ScenarioScreenshotService screenshots,
		IServiceProvider services)
	{
		_appSettings = appSettings;
		InitializeComponent();
		EnsureSingleInstance();
#if WINDOWS
		_mcp = services.GetRequiredService<GameLauncherWithGit.Infrastructure.Mcp.ScenarioMcpHost>();
	_playRecordingWeb = services.GetRequiredService<GameLauncherWithGit.Infrastructure.PlayRecordingWeb.PlayRecordingWebHost>();
		_mcpStartup = StartMcpAsync(services);
#endif
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var settings = _appSettings.Get();
		var window = new Window(new MainPage())
		{
			Title = "GameLauncherWithGit",
			Width = settings.WindowWidth,
			Height = settings.WindowHeight,
			MinimumWidth = AppSettings.WindowWidthMin,
			MinimumHeight = AppSettings.WindowHeightMin
		};

		window.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName is nameof(Window.Width) or nameof(Window.Height))
			{
				QueueWindowSizeSave(window);
			}
		};
#if WINDOWS
		window.Destroying += async (_, _) =>
		{
			try { await _playRecordingWeb.DisposeAsync(); }
			catch { Trace.WriteLine("Play recording Web shutdown failed."); }
			await _mcpStartup;
			try { await _mcp.DisposeAsync(); }
			catch { Trace.WriteLine("Scenario MCP shutdown failed."); }
		};
#endif

		return window;
	}

#if WINDOWS
	private async Task StartMcpAsync(IServiceProvider services)
	{
		try
		{
			var configuration = GameLauncherWithGit.Infrastructure.Mcp.ScenarioMcpConfiguration.Load(
				Path.Combine(GameLauncherWithGit.Application.Services.AppDataPaths.Directory, "scenario-mcp.json"));
			if (!configuration.Enabled) return;
			var store = services.GetRequiredService<GameLauncherWithGit.Infrastructure.Services.SqliteGameLibraryStore>();
			await _mcp.StartAsync(configuration, store, store, store);
		}
		catch { Trace.WriteLine("Scenario MCP unavailable. Check the local MCP configuration and port."); }
	}
#endif

	private void QueueWindowSizeSave(Window window)
	{
		if (window.Width <= 0 || window.Height <= 0)
		{
			return;
		}

		_windowSizeSaveCancellation?.Cancel();
		_windowSizeSaveCancellation?.Dispose();
		_windowSizeSaveCancellation = new CancellationTokenSource();
		var cancellationToken = _windowSizeSaveCancellation.Token;

		_ = SaveWindowSizeAfterDelayAsync(window, cancellationToken);
	}

	private async Task SaveWindowSizeAfterDelayAsync(Window window, CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(WindowSizeSaveDelayMilliseconds, cancellationToken);

			var width = (int)Math.Round(window.Width);
			var height = (int)Math.Round(window.Height);
			if (width <= 0 || height <= 0)
			{
				return;
			}

			var settings = _appSettings.Get() with
			{
				WindowWidth = width,
				WindowHeight = height
			};
			await _appSettings.SaveAsync(settings, cancellationToken);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Trace.WriteLine($"Window size save failed: {ex}");
		}
	}

	private static void EnsureSingleInstance()
	{
		if (!OperatingSystem.IsWindows() || GameLauncherWithGit.Application.Services.AppDataPaths.IsIsolated)
		{
			return;
		}

		singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
		if (createdNew)
		{
			AppDomain.CurrentDomain.ProcessExit += (_, _) =>
			{
				// アプリ終了時にミューテックスを解放する。
				singleInstanceMutex?.ReleaseMutex();
				singleInstanceMutex?.Dispose();
				singleInstanceMutex = null;
			};
			return;
		}

		// 既に起動済みの場合は新しいプロセスを終了する。
		if (!TryBringExistingInstanceToForeground())
		{
			Trace.WriteLine("Single-instance: failed to bring existing window to foreground.");
		}

		Environment.Exit(0);
	}

	private static bool TryBringExistingInstanceToForeground()
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		using var currentProcess = Process.GetCurrentProcess();
		var processes = Process.GetProcessesByName(currentProcess.ProcessName);
		try
		{
			foreach (var process in processes)
			{
				if (process.Id == currentProcess.Id)
				{
					continue;
				}

				var handle = TryGetWindowHandle(process);
				if (handle == IntPtr.Zero)
				{
					continue;
				}

				_ = ShowWindow(handle, SW_SHOW);
				_ = ShowWindow(handle, SW_RESTORE);
				if (SetForegroundWindow(handle) || GetForegroundWindow() == handle)
				{
					return true;
				}
			}
		}
		finally
		{
			foreach (var process in processes)
			{
				process.Dispose();
			}
		}

		return false;
	}

	private static IntPtr TryGetWindowHandle(Process process)
	{
		process.Refresh();
		if (process.MainWindowHandle != IntPtr.Zero)
		{
			return process.MainWindowHandle;
		}

		return FindTopLevelWindowByProcessId((uint)process.Id);
	}

	private static IntPtr FindTopLevelWindowByProcessId(uint processId)
	{
		IntPtr foundHandle = IntPtr.Zero;
		_ = EnumWindows((hwnd, _) =>
		{
			GetWindowThreadProcessId(hwnd, out var windowProcessId);
			if (windowProcessId != processId)
			{
				return true;
			}

			if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
			{
				return true;
			}

			foundHandle = hwnd;
			return false;
		}, IntPtr.Zero);

		return foundHandle;
	}

	private const uint GW_OWNER = 4;
	private const int SW_SHOW = 5;
	private const int SW_RESTORE = 9;

	private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll")]
	private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr GetForegroundWindow();
}
