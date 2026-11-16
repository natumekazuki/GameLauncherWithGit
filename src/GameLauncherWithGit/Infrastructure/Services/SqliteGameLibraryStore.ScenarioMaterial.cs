using GameLauncherWithGit.Application.Models;
using Microsoft.Data.Sqlite;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    public Task<ScenarioMaterialVersion> GetScenarioMaterialVersionAsync(long captureGameId, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: true);
            return await ReadScenarioMaterialVersionAsync(connection, transaction, captureGameId, cancellationToken);
        }, cancellationToken);

    private static async Task<ScenarioMaterialVersion> ReadScenarioMaterialVersionAsync(SqliteConnection connection,
        SqliteTransaction transaction, long captureGameId, CancellationToken cancellationToken = default)
    {
        await using var command = CaptureCommand(connection, transaction,
            "SELECT Revision,MatchingRevision FROM ScenarioMaterialState WHERE CaptureGameId=$game", ("$game", captureGameId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ArgumentException("ゲームの履歴が見つかりません。");
        return new(captureGameId, reader.GetInt64(0), reader.GetInt64(1));
    }

    public Task<ScenarioMaterialSnapshot> GetScenarioMaterialAsync(long captureGameId, ScenarioArchiveSelection selection,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await PrepareScenarioMaterialAsync(captureGameId, cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var ranges = await ReadCaptureRangesAsync(connection, transaction, captureGameId);
        var items = await ReadCaptureExportItemsAsync(connection, transaction, captureGameId, selection, ranges);
        return await ReadScenarioMaterialAsync(connection, transaction, captureGameId, selection, ranges, items, cancellationToken);
    }, cancellationToken);

    // Call before opening the export snapshot, not while holding the screenshot file gate.
    private async Task PrepareScenarioMaterialAsync(long captureGameId, CancellationToken cancellationToken = default)
    {
        ScenarioMatchingBatchResult result;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await ProcessScenarioMatchingBatchAsync(captureGameId, cancellationToken: cancellationToken);
            if (result.DeferredForCapture) { ScheduleScenarioMatching(captureGameId); break; }
            if (result.HasMore) await Task.Delay(1, cancellationToken);
        } while (result.HasMore);
    }

    private static async Task<ScenarioMaterialSnapshot> ReadScenarioMaterialAsync(SqliteConnection connection,
        SqliteTransaction transaction, long captureGameId, ScenarioArchiveSelection selection,
        IReadOnlyList<CaptureRange> ranges, IReadOnlyList<CaptureTextItem> items, CancellationToken cancellationToken = default)
    {
        var version = await ReadScenarioMaterialVersionAsync(connection, transaction, captureGameId, cancellationToken);
        bool dirty, complete;
        await using (var state = CaptureCommand(connection, transaction, """
            SELECT Dirty,NOT EXISTS(SELECT 1 FROM CaptureItems i JOIN CaptureSessions s ON s.Id=i.SessionId
                WHERE s.CaptureGameId=$game AND (i.SessionId,i.ReceiveSequence,i.StartUtf16)>(CursorSession,CursorSequence,CursorStart))
            FROM ScenarioMaterialState WHERE CaptureGameId=$game
            """, ("$game", captureGameId)))
        await using (var reader = await state.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken); dirty = reader.GetBoolean(0); complete = !dirty && reader.GetBoolean(1);
        }
        var groups = new Dictionary<(long CanonicalId, int RelativeStart, int Length), string>();
        var contents = new List<ScenarioContent>();
        var occurrences = new List<ScenarioOccurrence>();
        var imageMetadata = await ReadEventScreenshotMetadataAsync(connection, transaction, captureGameId,
            items.Select(x => new CaptureEventKey(x.SessionId, x.Sequence)).Distinct().ToArray(), cancellationToken);
        var eventImages = imageMetadata.ToDictionary(x => x.Event,
            x => (IReadOnlyList<ScenarioOccurrenceImage>)x.Images.Select(image => new ScenarioOccurrenceImage(image.ImageId, image.CapturedAt)).ToArray());
        // Only selected items are projected. Canonical IDs are private grouping keys, never output references.
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = item.Id;
            // Without a current index, retain the absolute slice offset in the private key.
            // Two equally sized selected slices of an unprocessed item are not interchangeable.
            var originalStart = 0;
            if (!dirty)
            {
                await using var indexed = CaptureCommand(connection, transaction, """
                    SELECT idx.CanonicalItemId,i.StartUtf16 FROM ScenarioSequenceIndex idx
                    JOIN CaptureItems i ON i.Id=idx.ItemId WHERE idx.ItemId=$id
                    """, ("$id", item.Id));
                await using var reader = await indexed.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) { canonical = reader.GetInt64(0); originalStart = reader.GetInt32(1); }
            }
            var key = (canonical, item.Start - originalStart, item.Length);
            if (!groups.TryGetValue(key, out var contentId))
            {
                contentId = "content-" + (contents.Count + 1);
                groups.Add(key, contentId);
                contents.Add(new(contentId, item.Text, item.Kind));
            }
            var eventKey = new CaptureEventKey(item.SessionId, item.Sequence);
            var images = eventImages[eventKey];
            var source = new ScenarioOccurrenceRef(captureGameId, item.SessionId, item.Sequence, item.Start, item.Length);
            occurrences.Add(new($"{captureGameId}:{item.SessionId}:{item.Sequence}:{item.Start}:{item.Length}", source,
                contentId, item.Speaker, item.LabelId, item.LabelName, item.ReceivedAt, images));
        }
        // Pair selected occurrences, including a selected revisit when its historical representative was excluded.
        var matches = occurrences.GroupBy(x => x.ContentId).Where(x => x.Count() > 1).Select(group =>
        {
            var first = group.First();
            return new ScenarioMatch(group.Key, group.Skip(1).Select(x => new ScenarioOccurrencePair(first.Source, x.Source)).ToArray());
        }).ToArray();
        return new(version, complete, contents, occurrences, matches);
    }
}
