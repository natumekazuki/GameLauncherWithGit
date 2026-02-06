using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using System.Text;

namespace GameLauncherWithGit.Application.Services;

public sealed class GameLibraryService : IGameLibraryService
{
	private readonly IGameLibraryStore _store;

	public GameLibraryService(IGameLibraryStore store)
	{
		_store = store;
	}

	public async Task<GameCardItem> CreateAsync(GameEditInput input, CancellationToken cancellationToken = default)
	{
		var normalizedInput = NormalizeInput(input);
		var gameId = await BuildUniqueGameIdAsync(normalizedInput.Title, cancellationToken);
		var game = new GameCardItem(
			Id: gameId,
			Title: normalizedInput.Title,
			ExecutablePath: normalizedInput.ExecutablePath,
			RelatedRepositoryPaths: normalizedInput.RelatedRepositoryPaths,
			LastPlayedAt: null,
			Status: GameCardStatus.Unknown);

		await _store.UpsertAsync(game, cancellationToken);
		return game;
	}

	public Task<GameCardItem?> FindByIdAsync(string gameId, CancellationToken cancellationToken = default)
	{
		return _store.FindByIdAsync(gameId, cancellationToken);
	}

	public Task<IReadOnlyList<GameCardItem>> GetGamesAsync(CancellationToken cancellationToken = default)
	{
		return _store.GetAllAsync(cancellationToken);
	}

	public async Task MarkLaunchedAsync(string gameId, CancellationToken cancellationToken = default)
	{
		var game = await _store.FindByIdAsync(gameId, cancellationToken);
		if (game is null)
		{
			return;
		}

		await _store.UpsertAsync(
			game with
			{
				LastPlayedAt = DateTimeOffset.Now
			},
			cancellationToken);
	}

	public async Task SetStatusAsync(string gameId, GameCardStatus status, CancellationToken cancellationToken = default)
	{
		var game = await _store.FindByIdAsync(gameId, cancellationToken);
		if (game is null)
		{
			return;
		}

		await _store.UpsertAsync(game with { Status = status }, cancellationToken);
	}

	public async Task<GameCardItem?> UpdateAsync(string gameId, GameEditInput input, CancellationToken cancellationToken = default)
	{
		var game = await _store.FindByIdAsync(gameId, cancellationToken);
		if (game is null)
		{
			return null;
		}

		var normalizedInput = NormalizeInput(input);
		var updated = game with
		{
			Title = normalizedInput.Title,
			ExecutablePath = normalizedInput.ExecutablePath,
			RelatedRepositoryPaths = normalizedInput.RelatedRepositoryPaths
		};

		await _store.UpsertAsync(updated, cancellationToken);
		return updated;
	}

	private async Task<string> BuildUniqueGameIdAsync(string title, CancellationToken cancellationToken)
	{
		var baseId = BuildSlug(title);
		var candidateId = baseId;
		var suffix = 2;

		while (await _store.FindByIdAsync(candidateId, cancellationToken) is not null)
		{
			candidateId = $"{baseId}-{suffix}";
			suffix++;
		}

		return candidateId;
	}

	private static GameEditInput NormalizeInput(GameEditInput input)
	{
		var title = input.Title.Trim();
		if (string.IsNullOrWhiteSpace(title))
		{
			throw new InvalidOperationException("タイトルは必須です。");
		}

		var executablePath = input.ExecutablePath.Trim();
		if (string.IsNullOrWhiteSpace(executablePath))
		{
			throw new InvalidOperationException("実行ファイルパスは必須です。");
		}

		var repositoryPaths = input.RelatedRepositoryPaths
			.Select(static value => value.Trim())
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		return new GameEditInput(title, executablePath, repositoryPaths);
	}

	private static string BuildSlug(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return $"game-{Guid.NewGuid():N}";
		}

		var builder = new StringBuilder(value.Length);
		var hasPreviousHyphen = false;
		foreach (var ch in value.Trim().ToLowerInvariant())
		{
			if (char.IsLetterOrDigit(ch))
			{
				builder.Append(ch);
				hasPreviousHyphen = false;
				continue;
			}

			if (!hasPreviousHyphen)
			{
				builder.Append('-');
				hasPreviousHyphen = true;
			}
		}

		var slug = builder.ToString().Trim('-');
		if (string.IsNullOrWhiteSpace(slug))
		{
			return $"game-{Guid.NewGuid():N}";
		}

		return slug;
	}
}
