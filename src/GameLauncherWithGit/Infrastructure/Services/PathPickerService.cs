using GameLauncherWithGit.Infrastructure.Abstractions;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class PathPickerService : IPathPickerService
{
	public Task<string?> PickExecutablePathAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult<string?>(null);
	}

	public Task<string?> PickThumbnailPathAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult<string?>(null);
	}
}
