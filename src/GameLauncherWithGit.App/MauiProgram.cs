using Microsoft.AspNetCore.Components.WebView.Maui;
using GameLauncherWithGit.App.Application.Abstractions;
using GameLauncherWithGit.App.Application.Services;
using GameLauncherWithGit.App.Infrastructure.Abstractions;
using GameLauncherWithGit.App.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncherWithGit.App;

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
#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
#endif

		builder.Services.AddSingleton<IAppStoragePaths, AppStoragePaths>();
		builder.Services.AddSingleton<IRepositoryWatcherService, RepositoryWatcherService>();
		builder.Services.AddSingleton<INotificationService, NotificationService>();
		builder.Services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();
		builder.Services.AddSingleton<IGameLibraryService, GameLibraryService>();
		builder.Services.AddScoped<ILauncherService, LauncherService>();
		builder.Services.AddScoped<IThumbnailService, ThumbnailService>();
		builder.Services.AddTransient<IGitService, GitService>();

		MauiApp app = builder.Build();
		app.Services.GetRequiredService<IAppStoragePaths>().EnsureCreated();
		return app;
	}
}
