using GameLauncherWithGit.Infrastructure.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IGitService
{
	Task<GitCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default);

	Task<GitCommandResult> RunAsync(
		string repositoryPath,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken = default);
}
