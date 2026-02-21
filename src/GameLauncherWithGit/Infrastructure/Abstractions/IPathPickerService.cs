namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IPathPickerService
{
	Task<string?> PickExecutablePathAsync(CancellationToken cancellationToken = default);

	Task<string?> PickRepositoryDirectoryPathAsync(CancellationToken cancellationToken = default);

	Task<string?> PickFolderPathAsync(string title, CancellationToken cancellationToken = default);

	Task<string?> PickThumbnailPathAsync(CancellationToken cancellationToken = default);
}
