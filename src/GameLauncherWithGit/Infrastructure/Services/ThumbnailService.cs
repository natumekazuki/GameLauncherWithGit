using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class ThumbnailService : IThumbnailService
{
	private readonly ILogger<ThumbnailService> _logger;

	public ThumbnailService(ILogger<ThumbnailService> logger)
	{
		_logger = logger;
	}

	public Task<string?> CreateThumbnailAsync(
		string sourceImagePath,
		CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Thumbnail placeholder called. sourceImagePath={SourceImagePath}", sourceImagePath);
		return Task.FromResult<string?>(null);
	}
}
