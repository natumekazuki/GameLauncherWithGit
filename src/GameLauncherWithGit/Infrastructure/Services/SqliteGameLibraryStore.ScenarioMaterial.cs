using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
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

    public async Task<ScenarioMaterialProjection> GetScenarioProjectionAsync(long captureGameId, ScenarioArchiveSelection selection,
        ScenarioMaterialAccessLevel level, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(level)) throw new ArgumentOutOfRangeException(nameof(level));
        return ScenarioMaterialWireAdapter.ToWire(await GetScenarioMaterialAsync(captureGameId, selection, cancellationToken), level);
    }

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
                contentId = ScenarioMaterialIds.Content(new(captureGameId, item.SessionId, item.Sequence, item.Start, item.Length));
                groups.Add(key, contentId);
                contents.Add(new(contentId, item.Text, item.Kind));
            }
            var eventKey = new CaptureEventKey(item.SessionId, item.Sequence);
            var images = eventImages[eventKey];
            var source = new ScenarioOccurrenceRef(captureGameId, item.SessionId, item.Sequence, item.Start, item.Length);
            occurrences.Add(new(ScenarioMaterialIds.Occurrence(source), source,
                contentId, item.Speaker, item.LabelId, item.LabelName, item.ReceivedAt, images));
        }
        // Pair selected occurrences, including a selected revisit when its historical representative was excluded.
        var matches = occurrences.GroupBy(x => x.ContentId).Where(x => x.Count() > 1).Select(group =>
        {
            var first = group.First();
            return new ScenarioMatch("match:" + group.Key, group.Skip(1).Select(x => new ScenarioOccurrencePair(first.Source, x.Source)).ToArray());
        }).ToArray();
        var includeAllRanges = selection.RangeId is null && selection.LabelId is null && !selection.UnclassifiedOnly
            && selection.Kind is null && string.IsNullOrWhiteSpace(selection.TextSearch) && string.IsNullOrWhiteSpace(selection.SpeakerSearch)
            && NormalizeCaptureTags(selection.IncludeTags).Length == 0 && NormalizeCaptureTags(selection.ExcludeTags).Length == 0;
        var selectedRanges = ranges.Select(range => new ScenarioMaterialRange(range.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            range.Name, range.Start, range.End, range.Tags,
            items.Select(item => (Item: item, Bounds: ClipCaptureInterval(item, range))).Where(x => x.Bounds.From < x.Bounds.To)
                .Select(x => new ScenarioOccurrenceRef(captureGameId, x.Item.SessionId, x.Item.Sequence,
                    x.Bounds.From, x.Bounds.To - x.Bounds.From)).ToArray()))
            .Where(range => includeAllRanges || range.Spans.Count > 0).ToArray();
        var classifications = items.Where(x => x.LabelId is not null).GroupBy(x => x.LabelId!.Value)
            .Select(group => new ScenarioMaterialClassification(group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                group.First().LabelName!, ScenarioMaterialWireAdapter.KindName(group.First().Kind))).ToArray();
        var imageIds = occurrences.SelectMany(x => x.Images).Select(x => x.ImageId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var imageAssets = await ReadScenarioMaterialImagesAsync(connection, transaction, imageIds, cancellationToken);
        var sceneFlow = await ReadScenarioSceneFlowAsync(connection, transaction, captureGameId, cancellationToken);
        // Preserve empty authored scenes in an unfiltered view. A filtered view contains only actual source intersections.
        var selectedRangeIds = selectedRanges.Select(x => long.Parse(x.Id, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var scenes = sceneFlow.Scenes.Select(scene => scene with
        {
            Definition = scene.Definition with
            { RangeIds = scene.Definition.RangeIds.Where(selectedRangeIds.Contains).Order().ToArray() }
        }).Where(scene => includeAllRanges || scene.Definition.RangeIds.Count > 0).OrderBy(x => x.Id).ToArray();
        var sceneIds = scenes.Select(x => x.Id).ToHashSet();
        var rangeTags = sceneFlow.RangeTags.Where(x => selectedRangeIds.Contains(x.RangeId)).ToArray();
        var tagIds = scenes.SelectMany(x => x.Definition.TagIds).Concat(rangeTags.Select(x => x.TagId)).ToHashSet();
        sceneFlow = sceneFlow with
        {
            Scenes = scenes, RangeTags = rangeTags,
            Tags = sceneFlow.Tags.Where(x => includeAllRanges || tagIds.Contains(x.Id)).OrderBy(x => x.Id).ToArray(),
            Connections = sceneFlow.Connections.Where(x => sceneIds.Contains(x.FromSceneId) && sceneIds.Contains(x.ToSceneId)).ToArray()
        };
        var snapshot = new ScenarioMaterialSnapshot(version, complete, contents, occurrences, matches)
        { Ranges = selectedRanges, Classifications = classifications, Images = imageAssets, SceneFlow = sceneFlow };
        var flowOverrides = await ReadScenarioFlowOverridesAsync(connection, transaction, captureGameId, cancellationToken);
        var selectionCuts = await ReadScenarioSelectionCutsAsync(connection, transaction, captureGameId, items, cancellationToken);
        return snapshot with { Graph = ScenarioFlowMaterialAdapter.Build(snapshot,
            flowOverrides with { Cuts = flowOverrides.Cuts.Concat(selectionCuts).Distinct().ToArray() }) };
    }

    private static async Task<IReadOnlyList<ScenarioMaterialImageMetadata>> ReadScenarioMaterialImagesAsync(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var images = new List<ScenarioMaterialImageMetadata>();
        foreach (var batch in ids.Chunk(400))
        {
            await using var query = connection.CreateCommand();
            query.Transaction = transaction;
            var parameters = batch.Select((id, index) => (Name: "$image" + index, Id: id)).ToArray();
            query.CommandText = $"SELECT Id,Width,Height,PixelHash FROM CaptureImages WHERE Id IN ({string.Join(',', parameters.Select(x => x.Name))}) ORDER BY Id";
            foreach (var parameter in parameters) query.Parameters.AddWithValue(parameter.Name, parameter.Id);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            var count = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                images.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
                count++;
            }
            if (count != batch.Length) throw new InvalidOperationException("添付画像のmetadataが見つかりません。");
        }
        return images;
    }

    private static async Task<IReadOnlyList<CapturePosition>> ReadScenarioSelectionCutsAsync(SqliteConnection connection,
        SqliteTransaction transaction, long game, IReadOnlyList<CaptureTextItem> selected, CancellationToken cancellationToken)
    {
        // Filtering may leave holes. Do not turn two selected but originally non-adjacent items into an observed edge.
        var original = new Dictionary<long, (int Ordinal, int Start, int End)>();
        await using (var query = CaptureCommand(connection, transaction, """
            SELECT i.Id,i.StartUtf16,i.LengthUtf16 FROM CaptureItems i JOIN CaptureSessions s ON s.Id=i.SessionId
            WHERE s.CaptureGameId=$game ORDER BY i.SessionId,i.ReceiveSequence,i.StartUtf16
            """, ("$game", game)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                original.Add(reader.GetInt64(0), (original.Count, reader.GetInt32(1), reader.GetInt32(1) + reader.GetInt32(2)));
        var cuts = new List<CapturePosition>();
        for (var index = 1; index < selected.Count; index++)
        {
            var previous = selected[index - 1]; var current = selected[index];
            if (previous.SessionId != current.SessionId) continue;
            var adjacent = previous.Id == current.Id
                ? previous.Start + previous.Length == current.Start
                : original[previous.Id].Ordinal + 1 == original[current.Id].Ordinal
                    && previous.Start + previous.Length == original[previous.Id].End && current.Start == original[current.Id].Start;
            if (!adjacent) cuts.Add(current.StartPosition);
        }
        return cuts;
    }
}
