using System.Collections.Concurrent;
using GameLauncherWithGit.App.Application.Abstractions;
using GameLauncherWithGit.App.Application.Models;
using GameLauncherWithGit.App.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.App.Application.Services;

public sealed class GameLibraryService : IGameLibraryService
{
    private readonly ConcurrentDictionary<Guid, GameItem> _items = new();
    private readonly IAppStoragePaths _appStoragePaths;
    private readonly IThumbnailService _thumbnailService;
    private readonly ILogger<GameLibraryService> _logger;

    public GameLibraryService(
        IAppStoragePaths appStoragePaths,
        IThumbnailService thumbnailService,
        ILogger<GameLibraryService> logger)
    {
        _appStoragePaths = appStoragePaths;
        _thumbnailService = thumbnailService;
        _logger = logger;
    }

    public Task<IReadOnlyList<GameItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GameItem> result = _items.Values
            .OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToArray();
        return Task.FromResult(result);
    }

    public async Task<GameItem> AddAsync(GameDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);

        var item = new GameItem
        {
            Id = Guid.NewGuid(),
            Title = draft.Title.Trim(),
            ExecutablePath = draft.ExecutablePath.Trim(),
            RelatedRepositoryIds = ParseRelatedRepositoryIds(draft.RelatedRepositoryIdsCsv),
            Status = GameSyncStatus.Unknown,
        };

        await TrySetThumbnailAsync(item, draft.ThumbnailSourcePath, cancellationToken).ConfigureAwait(false);

        _items[item.Id] = item;
        return Clone(item);
    }

    public async Task<GameItem> UpdateAsync(Guid gameId, GameDraft draft, CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);

        if (!_items.TryGetValue(gameId, out GameItem? existing))
        {
            throw new InvalidOperationException($"指定したゲームが存在しません: {gameId}");
        }

        existing.Title = draft.Title.Trim();
        existing.ExecutablePath = draft.ExecutablePath.Trim();
        existing.RelatedRepositoryIds = ParseRelatedRepositoryIds(draft.RelatedRepositoryIdsCsv);

        await TrySetThumbnailAsync(existing, draft.ThumbnailSourcePath, cancellationToken).ConfigureAwait(false);

        return Clone(existing);
    }

    public Task DeleteAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        if (_items.TryRemove(gameId, out GameItem? existing) && !string.IsNullOrWhiteSpace(existing.ThumbnailPath))
        {
            try
            {
                if (File.Exists(existing.ThumbnailPath))
                {
                    File.Delete(existing.ThumbnailPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "サムネイル削除に失敗: path={Path}", existing.ThumbnailPath);
            }
        }

        return Task.CompletedTask;
    }

    public Task MarkLaunchedAsync(Guid gameId, CancellationToken cancellationToken = default)
    {
        if (_items.TryGetValue(gameId, out GameItem? item))
        {
            item.LastPlayedAt = DateTimeOffset.Now;
        }

        return Task.CompletedTask;
    }

    private async Task TrySetThumbnailAsync(GameItem item, string? sourcePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        string destinationPath = Path.Combine(_appStoragePaths.ThumbnailDirectory, $"{item.Id}.png");
        bool success = await _thumbnailService
            .TryGenerateThumbnailAsync(sourcePath, destinationPath, cancellationToken)
            .ConfigureAwait(false);

        item.ThumbnailPath = success ? destinationPath : null;
    }

    private static IReadOnlyList<string> ParseRelatedRepositoryIds(string csv)
    {
        return csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ValidateDraft(GameDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            throw new InvalidOperationException("タイトルは必須です。");
        }

        if (string.IsNullOrWhiteSpace(draft.ExecutablePath))
        {
            throw new InvalidOperationException("実行ファイルパスは必須です。");
        }
    }

    private static GameItem Clone(GameItem item)
    {
        return new GameItem
        {
            Id = item.Id,
            Title = item.Title,
            ExecutablePath = item.ExecutablePath,
            ThumbnailPath = item.ThumbnailPath,
            RelatedRepositoryIds = item.RelatedRepositoryIds.ToArray(),
            LastPlayedAt = item.LastPlayedAt,
            Status = item.Status,
        };
    }
}
