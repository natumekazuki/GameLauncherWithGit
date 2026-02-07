namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IAutoStartService
{
	Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

	Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}
