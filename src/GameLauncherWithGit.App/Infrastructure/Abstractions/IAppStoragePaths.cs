namespace GameLauncherWithGit.App.Infrastructure.Abstractions;

public interface IAppStoragePaths
{
    string BaseDirectory { get; }

    string SettingsFilePath { get; }

    string LogDirectory { get; }

    string ThumbnailDirectory { get; }

    void EnsureCreated();
}
