using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Abstractions;

public interface IGameLibraryService
{
	Task<IReadOnlyList<GameCardItem>> GetGamesAsync(CancellationToken cancellationToken = default);

	Task<GameCardItem?> FindByIdAsync(string gameId, CancellationToken cancellationToken = default);

	Task MarkLaunchedAsync(string gameId, CancellationToken cancellationToken = default);

	Task SetStatusAsync(string gameId, GameCardStatus status, CancellationToken cancellationToken = default);
}
