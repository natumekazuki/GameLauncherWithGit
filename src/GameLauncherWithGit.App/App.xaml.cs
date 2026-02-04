using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.App;

public partial class App : Microsoft.Maui.Controls.Application
{
    private static bool _handlersRegistered;
    private static ILogger<App>? _logger;

    public App(ILogger<App> logger)
    {
        _logger = logger;
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
                _logger?.LogError("AppDomain.UnhandledException: {Payload}", payload);
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
                _logger?.LogError(args.Exception, "TaskScheduler.UnobservedTaskException");
                args.SetObserved();
            }
            catch
            {
                // 最後の防波堤。ここでは例外を再送出しない。
            }
        };
    }
}
