using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Abstractions;

public interface ISaveLinkService
{
	Task<IReadOnlyList<GameSaveLinkItem>> GetByGameIdAsync(
		string gameId,
		CancellationToken cancellationToken = default);

	IReadOnlyList<GameSaveLinkItem> NormalizeForGame(
		string gameId,
		IReadOnlyList<GameSaveLinkEditInput> links);

	void ValidateForGame(
		string gameId,
		IReadOnlyList<GameSaveLinkEditInput> links);

	Task<IReadOnlyList<GameSaveLinkItem>> UnlinkRemovedLinksAsync(
		string gameId,
		IReadOnlyList<GameSaveLinkItem> nextLinks,
		CancellationToken cancellationToken = default);

	Task<string?> RollbackUnlinkedLinksAsync(
		string gameId,
		IReadOnlyList<GameSaveLinkItem> unlinkedLinks,
		CancellationToken cancellationToken = default);

	Task ReplaceForGameAsync(
		string gameId,
		IReadOnlyList<GameSaveLinkEditInput> links,
		CancellationToken cancellationToken = default);

	Task<SaveLinkPrepareResult> EnsureReadyForLaunchAsync(
		string gameId,
		CancellationToken cancellationToken = default);
}
