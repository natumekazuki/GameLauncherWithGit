using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text;

namespace GameLauncherWithGit.Application.Services;

public sealed class GameLibraryService : IGameLibraryService
{
	private readonly IGameLibraryStore _store;
	private readonly IGitService _gitService;
	private readonly IThumbnailService _thumbnailService;
	private readonly ILogger<GameLibraryService> _logger;
	private readonly string _managedThumbnailDirectoryPath;

	public GameLibraryService(
		IGameLibraryStore store,
		IGitService gitService,
		IThumbnailService thumbnailService,
		ILogger<GameLibraryService> logger)
	{
		_store = store;
		_gitService = gitService;
		_thumbnailService = thumbnailService;
		_logger = logger;
		_managedThumbnailDirectoryPath = Path.GetFullPath(
			Path.Combine(FileSystem.AppDataDirectory, "thumbnails"));
	}

	public async Task<GameCardItem> CreateAsync(GameEditInput input, CancellationToken cancellationToken = default)
	{
		var normalizedInput = NormalizeInput(input);
		await EnsureRepositoryPathIsGitAsync(normalizedInput.RelatedRepositoryPath, cancellationToken);
		var gameId = await BuildUniqueGameIdAsync(normalizedInput.Title, cancellationToken);
		var thumbnailPath = await TryCreateThumbnailAsync(
			sourceImagePath: normalizedInput.ThumbnailSourcePath,
			fallbackPath: null,
			cancellationToken);

		var game = new GameCardItem(
			Id: gameId,
			Title: normalizedInput.Title,
			ExecutablePath: normalizedInput.ExecutablePath,
			RelatedRepositoryPath: normalizedInput.RelatedRepositoryPath,
			ThumbnailPath: thumbnailPath,
			LastPlayedAt: null,
			Status: GameCardStatus.Unknown,
			IsPinned: false);

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

	public async Task SetPinnedAsync(string gameId, bool isPinned, CancellationToken cancellationToken = default)
	{
		var game = await _store.FindByIdAsync(gameId, cancellationToken);
		if (game is null)
		{
			return;
		}

		await _store.UpsertAsync(game with { IsPinned = isPinned }, cancellationToken);
	}

	public async Task<bool> DeleteAsync(string gameId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return false;
		}

		var game = await _store.FindByIdAsync(gameId, cancellationToken);
		if (game is null)
		{
			return false;
		}

		await _store.DeleteAsync(gameId, cancellationToken);
		TryDeleteManagedThumbnail(game.ThumbnailPath);
		return true;
	}

	public async Task<GameCardItem?> UpdateAsync(string gameId, GameEditInput input, CancellationToken cancellationToken = default)
	{
		var game = await _store.FindByIdAsync(gameId, cancellationToken);
		if (game is null)
		{
			return null;
		}

		var normalizedInput = NormalizeInput(input);
		await EnsureRepositoryPathIsGitAsync(normalizedInput.RelatedRepositoryPath, cancellationToken);
		var thumbnailPath = normalizedInput.ClearThumbnail
			? null
			: await TryCreateThumbnailAsync(
				sourceImagePath: normalizedInput.ThumbnailSourcePath,
				fallbackPath: game.ThumbnailPath,
				cancellationToken);

		var updated = game with
		{
			Title = normalizedInput.Title,
			ExecutablePath = normalizedInput.ExecutablePath,
			RelatedRepositoryPath = normalizedInput.RelatedRepositoryPath,
			ThumbnailPath = thumbnailPath
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

		var repositoryPath = input.RelatedRepositoryPath?.Trim();
		if (string.IsNullOrWhiteSpace(repositoryPath))
		{
			repositoryPath = null;
		}

		var thumbnailSourcePath = input.ThumbnailSourcePath?.Trim();
		if (string.IsNullOrWhiteSpace(thumbnailSourcePath))
		{
			thumbnailSourcePath = null;
		}

		return new GameEditInput(
			Title: title,
			ExecutablePath: executablePath,
			RelatedRepositoryPath: repositoryPath,
			ThumbnailSourcePath: thumbnailSourcePath,
			ClearThumbnail: input.ClearThumbnail);
	}

	private async Task<string?> TryCreateThumbnailAsync(
		string? sourceImagePath,
		string? fallbackPath,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(sourceImagePath))
		{
			return fallbackPath;
		}

		try
		{
			return await _thumbnailService.CreateThumbnailAsync(sourceImagePath, cancellationToken) ?? fallbackPath;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Thumbnail generation failed. source={SourceImagePath}", sourceImagePath);
			return fallbackPath;
		}
	}

	private async Task EnsureRepositoryPathIsGitAsync(string? repositoryPath, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(repositoryPath))
		{
			return;
		}

		var result = await _gitService.RunAsync(repositoryPath, "rev-parse --is-inside-work-tree", cancellationToken);
		var output = FirstNonEmptyLine(result.StandardOutput);
		if (result.IsSuccess && string.Equals(output, "true", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		var reason = FirstNonEmptyLine(result.StandardError)
			?? output
			?? $"exit code: {result.ExitCode}";

		throw new InvalidOperationException(
			$"関連リポジトリとして登録できません。Git リポジトリを選択してください。path={repositoryPath}, reason={reason}");
	}

	private static string? FirstNonEmptyLine(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		return value
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
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

	private void TryDeleteManagedThumbnail(string? thumbnailPath)
	{
		if (string.IsNullOrWhiteSpace(thumbnailPath))
		{
			return;
		}

		string normalizedPath;
		try
		{
			normalizedPath = Path.GetFullPath(thumbnailPath.Trim());
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Thumbnail path normalization failed. path={ThumbnailPath}", thumbnailPath);
			return;
		}

		var managedDirectoryPrefix = _managedThumbnailDirectoryPath.EndsWith(Path.DirectorySeparatorChar)
			? _managedThumbnailDirectoryPath
			: _managedThumbnailDirectoryPath + Path.DirectorySeparatorChar;
		if (!normalizedPath.StartsWith(managedDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		try
		{
			if (File.Exists(normalizedPath))
			{
				File.Delete(normalizedPath);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to delete thumbnail file. path={ThumbnailPath}", normalizedPath);
		}
	}
}
