using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddSingleton<IRepositoryStateStore, RepositoryStateStore>();
		builder.Services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();
		builder.Services.AddSingleton<IRepositoryWatcherService, RepositoryWatcherService>();
		builder.Services.AddSingleton<INotificationService, NotificationService>();
		builder.Services.AddSingleton<ITrayService, TrayService>();
		builder.Services.AddSingleton<IAutoStartService, AutoStartService>();
		builder.Services.AddTransient<IGitService, GitService>();
		builder.Services.AddScoped<IThumbnailService, ThumbnailService>();
		builder.Services.AddScoped<ILauncherService, LauncherService>();
		builder.Services.AddScoped<IGameLibraryService, GameLibraryService>();
		builder.Services.AddScoped<IPathPickerService, PathPickerService>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
