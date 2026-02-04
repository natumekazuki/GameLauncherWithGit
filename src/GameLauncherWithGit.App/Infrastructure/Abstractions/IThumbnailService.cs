namespace GameLauncherWithGit.App.Infrastructure.Abstractions;

public interface IThumbnailService
{
    Task<bool> TryGenerateThumbnailAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
