using GameLauncherWithGit.App.Infrastructure.Models;

namespace GameLauncherWithGit.App.Infrastructure.Abstractions;

public interface IGitService
{
    Task<GitCommandResult> ExecuteAsync(
        string repositoryPath,
        string arguments,
        CancellationToken cancellationToken = default);
}
