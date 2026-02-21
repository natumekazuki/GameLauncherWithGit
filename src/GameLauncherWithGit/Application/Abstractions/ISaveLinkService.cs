using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Abstractions;

public interface ISaveLinkService
{
	Task<IReadOnlyList<GameSaveLinkItem>> GetByGameIdAsync(
		string gameId,
		CancellationToken cancellationToken = default);

	Task ReplaceForGameAsync(
		string gameId,
		IReadOnlyList<GameSaveLinkEditInput> links,
		CancellationToken cancellationToken = default);

	Task<SaveLinkPrepareResult> EnsureReadyForLaunchAsync(
		string gameId,
		CancellationToken cancellationToken = default);
}
