using GameLauncherWithGit.App.Infrastructure.Abstractions;

namespace GameLauncherWithGit.App.Infrastructure.Services;

public sealed class AppStoragePaths : IAppStoragePaths
{
    private const string AppDirectoryName = "GameLauncherWithGit";

    public AppStoragePaths()
    {
        BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppDirectoryName);
        SettingsFilePath = Path.Combine(BaseDirectory, "settings.json");
        LogDirectory = Path.Combine(BaseDirectory, "logs");
        ThumbnailDirectory = Path.Combine(BaseDirectory, "thumbnails");
    }

    public string BaseDirectory { get; }

    public string SettingsFilePath { get; }

    public string LogDirectory { get; }

    public string ThumbnailDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(BaseDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ThumbnailDirectory);
    }
}
