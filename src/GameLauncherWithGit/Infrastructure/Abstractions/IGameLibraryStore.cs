using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IGameLibraryStore
{
	Task<IReadOnlyList<GameCardItem>> GetAllAsync(CancellationToken cancellationToken = default);

	Task<GameCardItem?> FindByIdAsync(string gameId, CancellationToken cancellationToken = default);

	Task UpsertAsync(GameCardItem game, CancellationToken cancellationToken = default);

	Task UpsertWithSaveLinksAsync(
		GameCardItem game,
		IReadOnlyList<GameSaveLinkItem> links,
		CancellationToken cancellationToken = default);

	Task DeleteAsync(string gameId, CancellationToken cancellationToken = default);
}
