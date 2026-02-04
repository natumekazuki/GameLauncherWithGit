using GameLauncherWithGit.App.Infrastructure.Services;

namespace GameLauncherWithGit.App;

public partial class App : Microsoft.Maui.Controls.Application
{
    private static bool _handlersRegistered;

    public App()
    {
        RegisterGlobalExceptionHandlers();
        InitializeComponent();

        MainPage = new MainPage();
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        if (_handlersRegistered)
        {
            return;
        }

        _handlersRegistered = true;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                string payload = args.ExceptionObject?.ToString() ?? "Unknown unhandled exception";
                TemporaryErrorLogService.AppendMessage("AppDomain.UnhandledException", payload);
            }
            catch
            {
                // 最後の防波堤。ここでは例外を再送出しない。
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                TemporaryErrorLogService.AppendException("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved();
            }
            catch
            {
                // 最後の防波堤。ここでは例外を再送出しない。
            }
        };
    }
}
