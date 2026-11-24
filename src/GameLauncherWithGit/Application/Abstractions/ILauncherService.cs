using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Abstractions;

public interface ILauncherService
{
	/// <summary>
	/// Checks that the registered executable exists before modifying save links or running Git.
	/// Once the process starts, the result
	/// remains successful even if recording setup or launch metadata fails; the message identifies those failures.
	/// </summary>
	Task<LaunchResult> LaunchAsync(string gameId, CancellationToken cancellationToken = default);
}
