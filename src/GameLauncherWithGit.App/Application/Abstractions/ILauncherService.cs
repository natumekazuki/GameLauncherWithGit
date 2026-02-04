using GameLauncherWithGit.App.Application.Models;

namespace GameLauncherWithGit.App.Application.Abstractions;

public interface ILauncherService
{
    Task<LaunchResult> LaunchAsync(GameItem game, CancellationToken cancellationToken = default);
}
