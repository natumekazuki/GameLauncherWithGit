namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IThumbnailService
{
	Task<string?> CreateThumbnailAsync(
		string sourceImagePath,
		CancellationToken cancellationToken = default);
}
