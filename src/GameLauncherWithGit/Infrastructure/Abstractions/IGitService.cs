using GameLauncherWithGit.Infrastructure.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IGitService
{
	Task<GitCommandResult> RunAsync(
		string repositoryPath,
		string arguments,
		CancellationToken cancellationToken = default);
}
