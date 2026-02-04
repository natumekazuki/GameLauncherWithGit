namespace GameLauncherWithGit.App.Infrastructure.Abstractions;

public interface IPathPickerService
{
    Task<string?> PickExecutablePathAsync(CancellationToken cancellationToken = default);

    Task<string?> PickThumbnailImagePathAsync(CancellationToken cancellationToken = default);

    Task<string?> PickRepositoryFolderPathAsync(CancellationToken cancellationToken = default);
}
