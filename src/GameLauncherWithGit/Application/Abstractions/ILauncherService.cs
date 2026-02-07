using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Abstractions;

public interface ILauncherService
{
	Task<LaunchResult> LaunchAsync(string gameId, CancellationToken cancellationToken = default);
}
