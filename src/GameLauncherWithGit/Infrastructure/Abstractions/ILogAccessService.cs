namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface ILogAccessService
{
	Task AppendErrorAsync(string message, CancellationToken cancellationToken = default);

	Task OpenLatestErrorLogAsync(CancellationToken cancellationToken = default);

	Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default);
}
