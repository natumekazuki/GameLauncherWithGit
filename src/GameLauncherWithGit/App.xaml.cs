using System.Threading;

namespace GameLauncherWithGit;

public partial class App : Microsoft.Maui.Controls.Application
{
	private const string SingleInstanceMutexName = "GameLauncherWithGit.SingleInstance";
	private static Mutex? singleInstanceMutex;

	public App()
	{
		InitializeComponent();
		EnsureSingleInstance();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage()) { Title = "GameLauncherWithGit" };
	}

	private static void EnsureSingleInstance()
	{
		if (!OperatingSystem.IsWindows())
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
		Environment.Exit(0);
	}
}
