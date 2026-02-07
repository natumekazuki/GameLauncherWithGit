using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using MonochromeMemory.Log.Core;
using MonochromeMemory.Log.Sinks.File.Extensions;

namespace GameLauncherWithGit;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		var logsDirectoryPath = Path.Combine(FileSystem.AppDataDirectory, "logs");
		var structuredLogPath = Path.Combine(logsDirectoryPath, "app-events.jsonl");

		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services
			.AddMonochromeMemoryLogCore()
			.AddFileLogSink(
				configure: options =>
				{
					options.MinimumSeverity = LogLevel.Information;
					options.Buffering = new DispatchBufferOptions
					{
						Mode = BufferingMode.Bounded,
						Capacity = 2048,
						OverflowStrategy = BufferOverflowStrategy.DropOld,
						MaxBatchSize = 64,
						MaxFlushInterval = TimeSpan.FromMilliseconds(500)
					};
				},
				configureFile: options => options.FilePath = structuredLogPath);

		builder.Services.AddSingleton<IRepositoryStateStore, RepositoryStateStore>();
		builder.Services.AddSingleton<IGameLibraryStore, SqliteGameLibraryStore>();
		builder.Services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();
		builder.Services.AddSingleton<IRepositoryWatcherService, RepositoryWatcherService>();
		builder.Services.AddSingleton<INotificationService, NotificationService>();
		builder.Services.AddSingleton<ITrayService, TrayService>();
		builder.Services.AddSingleton<IAutoStartService, AutoStartService>();
		builder.Services.AddSingleton<ILogAccessService, LogAccessService>();
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
