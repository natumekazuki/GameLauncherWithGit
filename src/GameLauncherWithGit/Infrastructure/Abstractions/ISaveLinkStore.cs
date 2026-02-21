using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface ISaveLinkStore
{
	Task<IReadOnlyList<GameSaveLinkItem>> GetByGameIdAsync(
		string gameId,
		CancellationToken cancellationToken = default);

	Task ReplaceByGameIdAsync(
		string gameId,
		IReadOnlyList<GameSaveLinkItem> links,
		CancellationToken cancellationToken = default);

	Task DeleteByGameIdAsync(
		string gameId,
		CancellationToken cancellationToken = default);
}
