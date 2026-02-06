using GameLauncherWithGit.Infrastructure.Abstractions;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class AutoStartService : IAutoStartService
{
	private bool _enabled;

	public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult(_enabled);
	}

	public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
	{
		_enabled = enabled;
		return Task.CompletedTask;
	}
}
