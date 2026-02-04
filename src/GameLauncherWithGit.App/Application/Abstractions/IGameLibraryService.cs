using GameLauncherWithGit.App.Application.Models;

namespace GameLauncherWithGit.App.Application.Abstractions;

public interface IGameLibraryService
{
    Task<IReadOnlyList<GameItem>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<GameItem> AddAsync(GameDraft draft, CancellationToken cancellationToken = default);

    Task<GameItem> UpdateAsync(Guid gameId, GameDraft draft, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid gameId, CancellationToken cancellationToken = default);

    Task MarkLaunchedAsync(Guid gameId, CancellationToken cancellationToken = default);
}
