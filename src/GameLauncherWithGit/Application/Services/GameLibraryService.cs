using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using System.Collections.Concurrent;

namespace GameLauncherWithGit.Application.Services;

public sealed class GameLibraryService : IGameLibraryService
{
	private readonly ConcurrentDictionary<string, GameCardItem> _games = new(StringComparer.OrdinalIgnoreCase);

	public GameLibraryService()
	{
		var seedItems = new[]
		{
			new GameCardItem(
				Id: "elden-ring",
				Title: "Elden Ring",
				ExecutablePath: @"C:\Games\EldenRing\Game\eldenring.exe",
				RelatedRepositoryPaths: Array.Empty<string>(),
				LastPlayedAt: DateTimeOffset.Now.AddDays(-1),
				Status: GameCardStatus.Synced),
			new GameCardItem(
				Id: "monster-hunter-wilds",
				Title: "Monster Hunter Wilds",
				ExecutablePath: @"C:\Games\MonsterHunterWilds\mhwilds.exe",
				RelatedRepositoryPaths: Array.Empty<string>(),
				LastPlayedAt: null,
				Status: GameCardStatus.Unknown),
			new GameCardItem(
				Id: "hades-ii",
				Title: "Hades II",
				ExecutablePath: @"C:\Games\HadesII\hades2.exe",
				RelatedRepositoryPaths: Array.Empty<string>(),
				LastPlayedAt: DateTimeOffset.Now.AddDays(-3),
				Status: GameCardStatus.Error)
		};

		foreach (var item in seedItems)
		{
			_games[item.Id] = item;
		}
	}

	public Task<GameCardItem?> FindByIdAsync(string gameId, CancellationToken cancellationToken = default)
	{
		_games.TryGetValue(gameId, out var game);
		return Task.FromResult(game);
	}

	public Task<IReadOnlyList<GameCardItem>> GetGamesAsync(CancellationToken cancellationToken = default)
	{
		var snapshot = _games.Values
			.OrderByDescending(static item => item.LastPlayedAt)
			.ThenBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return Task.FromResult<IReadOnlyList<GameCardItem>>(snapshot);
	}

	public Task MarkLaunchedAsync(string gameId, CancellationToken cancellationToken = default)
	{
		if (_games.TryGetValue(gameId, out var game))
		{
			_games[gameId] = game with { LastPlayedAt = DateTimeOffset.Now };
		}

		return Task.CompletedTask;
	}

	public Task SetStatusAsync(string gameId, GameCardStatus status, CancellationToken cancellationToken = default)
	{
		if (_games.TryGetValue(gameId, out var game))
		{
			_games[gameId] = game with { Status = status };
		}

		return Task.CompletedTask;
	}
}
