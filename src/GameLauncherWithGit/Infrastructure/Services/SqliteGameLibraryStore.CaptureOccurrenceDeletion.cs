using GameLauncherWithGit.Application.Models;
using Microsoft.Data.Sqlite;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    /// <summary>Accepts a clipped span only when both endpoints belong to the identified saved body item.</summary>
    public async Task<CaptureOccurrenceDeletionTarget> GetCaptureOccurrenceDeletionTargetAsync(long recordId, CaptureOccurrenceDeletionRequest request)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        var current = await FindDeletionItemAsync(connection, null, recordId, request.ItemId)
            ?? throw OccurrenceChanged();
        // The positions must be contained by the same canonical item; text is never an identity.
        if (request.Start.SessionId != current.SessionId || request.End.SessionId != current.SessionId ||
            request.Start.Sequence != current.Sequence || request.End.Sequence != current.Sequence ||
            request.Start.Offset < current.Start || request.End.Offset > current.EndPosition.Offset ||
            request.Start.Offset >= request.End.Offset)
            throw OccurrenceChanged();
        return new(recordId, current);
    }

    /// <summary>Records the deleted source interval and removes one item atomically, then cleans up detached images.</summary>
    public async Task<CaptureDeletionResult> DeleteCaptureOccurrenceAsync(long recordId, CaptureOccurrenceDeletionTarget target)
    {
        if (target.RecordId != recordId) throw OccurrenceChanged();
        await _screenshotFilesLock.WaitAsync();
        try
        {
            await EnsureInitializedAsync(default);
            await using var connection = await OpenConnectionAsync(default);
            await using var transaction = connection.BeginTransaction();
            var expected = target.Item;
            var item = await FindDeletionItemAsync(connection, transaction, recordId, expected.Id);
            // A re-split may leave identical text at these offsets, but a new item is not the confirmed item.
            if (item is null || item.StartPosition != expected.StartPosition || item.Length != expected.Length)
                throw OccurrenceChanged();

            await using (var suppress = CaptureCommand(connection, transaction, """
                INSERT INTO CaptureDeletedSpans(SessionId,ReceiveSequence,StartUtf16,EndUtf16)
                VALUES($session,$sequence,$start,$end)
                """, ("$session", item.SessionId), ("$sequence", item.Sequence),
                ("$start", item.Start), ("$end", item.EndPosition.Offset)))
                await suppress.ExecuteNonQueryAsync();
            await using (var remove = CaptureCommand(connection, transaction,
                "DELETE FROM CaptureItems WHERE Id=$id", ("$id", item.Id)))
                if (await remove.ExecuteNonQueryAsync() != 1) throw OccurrenceChanged();

            // Images belong to the received event, so all surviving siblings retain the same attachments.
            var candidates = await DetachImagesWithoutItemsAsync(connection, transaction, item.SessionId, item.Sequence);
            await transaction.CommitAsync();
            ScheduleScenarioMatching(recordId);
            return new(await CleanupUnreferencedScreenshotsCoreAsync(connection, candidates));
        }
        finally { _screenshotFilesLock.Release(); }
    }

    /// <summary>Reads the canonical row under the specified recording root, excluding non-body events.</summary>
    private static async Task<CaptureTextItem?> FindDeletionItemAsync(SqliteConnection connection, SqliteTransaction? transaction,
        long recordId, long itemId)
    {
        await using var command = CaptureCommand(connection, transaction, CaptureItemQuery + "\n" + """
            JOIN CaptureSessions s ON s.Id=i.SessionId
            WHERE s.CaptureGameId=$record AND i.Id=$item AND e.InputRole=1
            """, ("$record", recordId), ("$item", itemId));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadCaptureItem(reader) : null;
    }

    /// <summary>Signals that confirmation must be rebuilt from the currently displayed row.</summary>
    private static InvalidOperationException OccurrenceChanged() =>
        new("削除対象の本文が変わりました。本文を読み込み直して、行を選び直してください。");

    /// <summary>When the last item is removed, detaches its event's images and returns only newly orphaned files.</summary>
    private static async Task<List<string>> DetachImagesWithoutItemsAsync(SqliteConnection connection, SqliteTransaction transaction,
        long sessionId, long sequence)
    {
        await using var siblings = CaptureCommand(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM CaptureItems WHERE SessionId=$session AND ReceiveSequence=$sequence)
            """, ("$session", sessionId), ("$sequence", sequence));
        if ((long)(await siblings.ExecuteScalarAsync())! != 0) return [];

        var candidates = new List<string>();
        await using (var query = CaptureCommand(connection, transaction, """
            SELECT mine.ImageId FROM CaptureEventImages mine
            WHERE mine.SessionId=$session AND mine.ReceiveSequence=$sequence AND NOT EXISTS(
                SELECT 1 FROM CaptureEventImages other WHERE other.ImageId=mine.ImageId
                AND (other.SessionId<>$session OR other.ReceiveSequence<>$sequence))
            """, ("$session", sessionId), ("$sequence", sequence)))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) candidates.Add(reader.GetString(0));
        await using var detach = CaptureCommand(connection, transaction, """
            DELETE FROM CaptureEventImages WHERE SessionId=$session AND ReceiveSequence=$sequence
            """, ("$session", sessionId), ("$sequence", sequence));
        await detach.ExecuteNonQueryAsync();
        return candidates;
    }

    private readonly record struct DeletedCaptureSpan(int Start, int End);

    /// <summary>Reads the persistent exclusions for an event whose materialized items are about to be rebuilt.</summary>
    private static async Task<IReadOnlyList<DeletedCaptureSpan>> ReadDeletedCaptureSpansAsync(SqliteConnection connection,
        SqliteTransaction transaction, long sessionId, long sequence)
    {
        await using var query = CaptureCommand(connection, transaction, """
            SELECT StartUtf16,EndUtf16 FROM CaptureDeletedSpans
            WHERE SessionId=$session AND ReceiveSequence=$sequence ORDER BY StartUtf16
            """, ("$session", sessionId), ("$sequence", sequence));
        var result = new List<DeletedCaptureSpan>();
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetInt32(0), reader.GetInt32(1)));
        return result;
    }

    /// <summary>Retains the automatic classification for each surviving slice of a rebuilt segment.</summary>
    private static IEnumerable<CaptureSegment> ExcludeDeletedCaptureSegments(IEnumerable<CaptureSegment> segments,
        IReadOnlyList<DeletedCaptureSpan> deleted)
    {
        foreach (var segment in segments)
            foreach (var (start, length) in RetainedCaptureSlices(segment.Start, segment.Start + segment.Length, deleted))
                yield return new(start, length, segment.Kind);
    }

    /// <summary>Subtracts deleted half-open intervals without shifting the original UTF-16 coordinate system.</summary>
    private static IEnumerable<(int Start, int Length)> RetainedCaptureSlices(int start, int end, IReadOnlyList<DeletedCaptureSpan> deleted)
    {
        foreach (var span in deleted)
        {
            if (span.End <= start) continue;
            if (span.Start >= end) break;
            if (span.Start > start) yield return (start, span.Start - start);
            start = span.End;
        }
        if (start < end) yield return (start, end - start);
    }
}
