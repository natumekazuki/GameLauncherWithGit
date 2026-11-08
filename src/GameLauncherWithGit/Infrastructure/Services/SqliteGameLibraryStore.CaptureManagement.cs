using GameLauncherWithGit.Application.Models;
using Microsoft.Data.Sqlite;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    public async Task<IReadOnlyList<CaptureDeletionTarget>> GetCaptureDeletionTargetsAsync(long profileId, IReadOnlyCollection<long> itemIds)
    {
        if (itemIds.Count == 0) throw new ArgumentException("削除する本文を選んでください。");
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        var targets = new Dictionary<CaptureEventKey, CaptureDeletionTarget>();
        foreach (var id in itemIds.Distinct())
        {
            await using var command = CaptureCommand(connection, transaction, """
                SELECT e.SessionId,e.ReceiveSequence,e.RawText,
                    (SELECT COUNT(*) FROM CaptureItems part WHERE part.SessionId=e.SessionId AND part.ReceiveSequence=e.ReceiveSequence)
                FROM CaptureItems i JOIN CaptureEvents e ON e.SessionId=i.SessionId AND e.ReceiveSequence=i.ReceiveSequence
                JOIN CaptureSessions s ON s.Id=e.SessionId
                WHERE i.Id=$id AND s.CaptureGameId=$game AND e.InputRole=1
                """, ("$id", id), ("$game", profileId));
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new ArgumentException("削除対象の本文が見つかりません。選択し直してください。");
            var key = new CaptureEventKey(reader.GetInt64(0), reader.GetInt64(1));
            targets[key] = new(key, reader.GetString(2), reader.GetInt32(3));
        }
        await transaction.CommitAsync();
        return targets.Values.OrderBy(x => x.Key.SessionId).ThenBy(x => x.Key.Sequence).ToArray();
    }

    public async Task<CaptureDeletionResult> DeleteCaptureEventsAsync(long profileId, IReadOnlyCollection<CaptureEventKey> events)
    {
        await _screenshotFilesLock.WaitAsync();
        try { return await DeleteCaptureEventsCoreAsync(profileId, events); }
        finally { _screenshotFilesLock.Release(); }
    }

    private async Task<CaptureDeletionResult> DeleteCaptureEventsCoreAsync(long profileId, IReadOnlyCollection<CaptureEventKey> events)
    {
        if (events.Count == 0) throw new ArgumentException("削除する原文を選んでください。");
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        foreach (var key in events.Distinct())
        {
            await using var find = CaptureCommand(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM CaptureEvents e JOIN CaptureSessions s ON s.Id=e.SessionId
                WHERE s.CaptureGameId=$game AND e.SessionId=$session AND e.ReceiveSequence=$sequence AND e.InputRole=1)
                """, ("$game", profileId), ("$session", key.SessionId), ("$sequence", key.Sequence));
            if ((long)(await find.ExecuteScalarAsync())! == 0)
                throw new ArgumentException("削除対象の原文が見つかりません。選択し直してください。");
            await using var delete = CaptureCommand(connection, transaction, """
                DELETE FROM CaptureItems WHERE SessionId=$session AND ReceiveSequence=$sequence;
                DELETE FROM CaptureEvents WHERE SessionId=$session AND ReceiveSequence=$sequence;
                """, ("$session", key.SessionId), ("$sequence", key.Sequence));
            await delete.ExecuteNonQueryAsync();
        }
        var candidates = await ReadUnreferencedScreenshotsAsync(connection, transaction);
        await transaction.CommitAsync();
        return new(await CleanupUnreferencedScreenshotsCoreAsync(connection, candidates));
    }

    public async Task<CaptureHistoryCounts> GetCaptureHistoryCountsAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null, """
            SELECT (SELECT COUNT(*) FROM CaptureItems i JOIN CaptureSessions s ON s.Id=i.SessionId WHERE s.CaptureGameId=g.Id),
                (SELECT COUNT(*) FROM CaptureEvents e JOIN CaptureSessions s ON s.Id=e.SessionId WHERE s.CaptureGameId=g.Id),
                (SELECT COUNT(*) FROM CaptureRanges r WHERE r.CaptureGameId=g.Id)
            FROM CaptureGames g WHERE g.Id=$game
            """, ("$game", profileId));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new ArgumentException("ゲームの履歴が見つかりません。");
        return new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public async Task<CaptureDeletionResult> ClearCaptureHistoryAsync(long profileId)
    {
        await _screenshotFilesLock.WaitAsync();
        try { return await ClearCaptureHistoryCoreAsync(profileId); }
        finally { _screenshotFilesLock.Release(); }
    }

    private async Task<CaptureDeletionResult> ClearCaptureHistoryCoreAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        await using var find = CaptureCommand(connection, transaction, "SELECT EXISTS(SELECT 1 FROM CaptureGames WHERE Id=$game)", ("$game", profileId));
        if ((long)(await find.ExecuteScalarAsync())! == 0) throw new ArgumentException("ゲームの履歴が見つかりません。");
        await using var command = CaptureCommand(connection, transaction, """
            DELETE FROM CaptureRanges WHERE CaptureGameId=$game;
            DELETE FROM CaptureItems WHERE SessionId IN (SELECT Id FROM CaptureSessions WHERE CaptureGameId=$game);
            DELETE FROM CaptureEvents WHERE SessionId IN (SELECT Id FROM CaptureSessions WHERE CaptureGameId=$game);
            DELETE FROM CaptureSources WHERE SessionId IN (SELECT Id FROM CaptureSessions WHERE CaptureGameId=$game);
            DELETE FROM CaptureSessions WHERE CaptureGameId=$game;
            """, ("$game", profileId));
        await command.ExecuteNonQueryAsync();
        var candidates = await ReadUnreferencedScreenshotsAsync(connection, transaction);
        await transaction.CommitAsync();
        return new(await CleanupUnreferencedScreenshotsCoreAsync(connection, candidates));
    }

    public async Task UpdateCaptureRangeAsync(long profileId, long rangeId, string name, IReadOnlyCollection<string> tags)
    {
        var normalizedTags = NormalizeCaptureTags(tags);
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("区間名を1行で入力してください。");
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        await using var command = CaptureCommand(connection, transaction,
            "UPDATE CaptureRanges SET Name=$name WHERE Id=$id AND CaptureGameId=$game", ("$name", name), ("$id", rangeId), ("$game", profileId));
        if (await command.ExecuteNonQueryAsync() != 1) throw new ArgumentException("このゲームの区間を選んでください。");
        await WriteCaptureTagsAsync(connection, transaction, rangeId, normalizedTags);
        await transaction.CommitAsync();
    }

    public async Task DeleteCaptureRangeAsync(long profileId, long rangeId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null,
            "DELETE FROM CaptureRanges WHERE Id=$id AND CaptureGameId=$game", ("$id", rangeId), ("$game", profileId));
        if (await command.ExecuteNonQueryAsync() != 1) throw new ArgumentException("このゲームの区間を選んでください。");
    }
}
