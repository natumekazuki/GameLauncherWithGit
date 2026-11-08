using GameLauncherWithGit.Application.Models;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    public async Task<IReadOnlyList<CaptureRange>> GetCaptureRangesAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var ranges = await ReadCaptureRangesAsync(connection, transaction, profileId);
        await transaction.CommitAsync();
        return ranges;
    }

    private static async Task<IReadOnlyList<CaptureRange>> ReadCaptureRangesAsync(SqliteConnection connection, SqliteTransaction transaction, long profileId)
    {
        await using var command = CaptureCommand(connection, transaction, """
            SELECT Id,Name,StartSessionId,StartSequence,StartOffset,EndSessionId,EndSequence,EndOffset
            FROM CaptureRanges WHERE CaptureGameId=$game ORDER BY StartSessionId,StartSequence,StartOffset,Id
            """, ("$game", profileId));
        await using var reader = await command.ExecuteReaderAsync();
        var ranges = new List<CaptureRange>();
        while (await reader.ReadAsync()) ranges.Add(new(reader.GetInt64(0), profileId, reader.GetString(1),
            new(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4)), new(reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7))));
        await reader.DisposeAsync();
        await using var tagsQuery = CaptureCommand(connection, transaction, """
            SELECT t.RangeId,t.Tag FROM CaptureRangeTags t JOIN CaptureRanges r ON r.Id=t.RangeId
            WHERE r.CaptureGameId=$game ORDER BY t.Tag
            """, ("$game", profileId));
        await using var tagsReader = await tagsQuery.ExecuteReaderAsync();
        var tags = new Dictionary<long, List<string>>();
        while (await tagsReader.ReadAsync())
        {
            var id = tagsReader.GetInt64(0);
            if (!tags.TryGetValue(id, out var values)) tags[id] = values = [];
            values.Add(tagsReader.GetString(1));
        }
        return ranges.Select(x => x with { Tags = tags.TryGetValue(x.Id, out var values) ? values.Order(StringComparer.Ordinal).ToArray() : [] }).ToArray();
    }

    private static string[] NormalizeCaptureTags(IEnumerable<string>? tags)
    {
        return (tags ?? []).Select(tag =>
        {
            var value = tag?.Trim();
            if (string.IsNullOrEmpty(value) || value.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("タグを空でない1行で入力してください。");
            return value;
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task WriteCaptureTagsAsync(SqliteConnection connection, SqliteTransaction transaction, long rangeId, string[] tags)
    {
        await using var delete = CaptureCommand(connection, transaction, "DELETE FROM CaptureRangeTags WHERE RangeId=$id", ("$id", rangeId));
        await delete.ExecuteNonQueryAsync();
        foreach (var tag in tags)
        {
            await using var insert = CaptureCommand(connection, transaction,
                "INSERT INTO CaptureRangeTags(RangeId,Tag) VALUES($id,$tag)", ("$id", rangeId), ("$tag", tag));
            await insert.ExecuteNonQueryAsync();
        }
    }

    public async Task<long> SaveCaptureRangeAsync(long profileId, string name, CapturePosition start, CapturePosition end, IReadOnlyCollection<string>? tags = null)
    {
        var normalizedTags = NormalizeCaptureTags(tags);
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("区間名を1行で入力してください。");
        if (start.CompareTo(end) >= 0) throw new ArgumentException("開始より後の終了位置を選んでください。");
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        foreach (var position in new[] { start, end })
        {
            if (await CaptureOwnerAsync(connection, transaction, position.SessionId) != profileId)
                throw new ArgumentException("同じゲームの本文を選んでください。");
            if (position.Sequence == 0 && position.Offset == 0) continue;
            await using var find = CaptureCommand(connection, transaction,
                "SELECT RawText FROM CaptureEvents WHERE SessionId=$session AND ReceiveSequence=$sequence AND InputRole=1",
                ("$session", position.SessionId), ("$sequence", position.Sequence));
            var raw = await find.ExecuteScalarAsync() as string ?? throw new ArgumentException("区間の本文が保存されていません。");
            if (position.Offset < 0 || position.Offset > raw.Length
                || !StringInfo.ParseCombiningCharacters(raw).Append(raw.Length).Contains(position.Offset))
                throw new ArgumentException("本文の文字の境界を選んでください。");
        }
        await using var exists = CaptureCommand(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM CaptureItems i JOIN CaptureSessions s ON s.Id=i.SessionId
            WHERE s.CaptureGameId=$game
            AND (i.SessionId,i.ReceiveSequence,i.StartUtf16+i.LengthUtf16)>($ss,$sq,$so)
            AND (i.SessionId,i.ReceiveSequence,i.StartUtf16)<($es,$eq,$eo))
            """, ("$game", profileId), ("$ss", start.SessionId), ("$sq", start.Sequence), ("$so", start.Offset),
            ("$es", end.SessionId), ("$eq", end.Sequence), ("$eo", end.Offset));
        if ((long)(await exists.ExecuteScalarAsync())! == 0) throw new ArgumentException("本文を含む区間を選んでください。");
        await using var save = CaptureCommand(connection, transaction, """
            INSERT INTO CaptureRanges(CaptureGameId,Name,StartSessionId,StartSequence,StartOffset,EndSessionId,EndSequence,EndOffset)
            VALUES($game,$name,$ss,$sq,$so,$es,$eq,$eo) RETURNING Id
            """, ("$game", profileId), ("$name", name), ("$ss", start.SessionId), ("$sq", start.Sequence), ("$so", start.Offset),
            ("$es", end.SessionId), ("$eq", end.Sequence), ("$eo", end.Offset));
        var id = (long)(await save.ExecuteScalarAsync())!;
        await WriteCaptureTagsAsync(connection, transaction, id, normalizedTags);
        await transaction.CommitAsync();
        return id;
    }

    public async Task<IReadOnlyList<string>> GetGameCaptureSpeakersAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null, "SELECT DISTINCT Speaker FROM (" + CaptureItemQuery + """
             JOIN CaptureSessions s ON s.Id=i.SessionId WHERE s.CaptureGameId=$game
            ) WHERE Speaker IS NOT NULL ORDER BY Speaker
            """, ("$game", profileId));
        await using var reader = await command.ExecuteReaderAsync();
        var speakers = new List<string>();
        while (await reader.ReadAsync())
        {
            var speaker = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(speaker)) speakers.Add(speaker);
        }
        return speakers;
    }

    public async Task<IReadOnlyList<CaptureTextItem>> GetCaptureExportItemsAsync(long profileId, ScenarioArchiveSelection selection)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var ranges = await ReadCaptureRangesAsync(connection, transaction, profileId);
        var items = await ReadCaptureExportItemsAsync(connection, transaction, profileId, selection, ranges);
        await transaction.CommitAsync();
        return items;
    }

    private static async Task<IReadOnlyList<CaptureTextItem>> ReadCaptureExportItemsAsync(SqliteConnection connection,
        SqliteTransaction transaction, long profileId, ScenarioArchiveSelection selection, IReadOnlyList<CaptureRange> ranges)
    {
        var range = selection.RangeId is null ? null : ranges.SingleOrDefault(x => x.Id == selection.RangeId)
            ?? throw new ArgumentException("このゲームの区間を選んでください。");
        var includeTags = NormalizeCaptureTags(selection.IncludeTags);
        var excludeTags = NormalizeCaptureTags(selection.ExcludeTags);
        var included = ranges.Where(x => x.Tags.Intersect(includeTags, StringComparer.Ordinal).Any()).ToArray();
        var excluded = ranges.Where(x => x.Tags.Intersect(excludeTags, StringComparer.Ordinal).Any()).ToArray();
        var textSearch = string.IsNullOrWhiteSpace(selection.TextSearch) ? null : selection.TextSearch.Trim();
        var speakerSearch = string.IsNullOrWhiteSpace(selection.SpeakerSearch) ? null : selection.SpeakerSearch.Trim();
        await using var command = CaptureCommand(connection, transaction, CaptureItemQuery + """
             JOIN CaptureSessions s ON s.Id=i.SessionId WHERE s.CaptureGameId=$game
             AND ($label IS NULL OR l.Id=$label) AND ($unclassified=0 OR l.Id IS NULL) AND ($kind IS NULL OR l.Kind=$kind)
             ORDER BY i.SessionId,i.ReceiveSequence,i.StartUtf16
            """, ("$game", profileId), ("$label", selection.LabelId), ("$unclassified", selection.UnclassifiedOnly),
            ("$kind", selection.Kind is null ? null : (int)selection.Kind));
        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<CaptureTextItem>();
        while (await reader.ReadAsync())
        {
            var item = ReadCaptureItem(reader);
            if (speakerSearch is not null && item.Speaker?.Contains(speakerSearch, StringComparison.OrdinalIgnoreCase) != true) continue;
            var bounds = ClipCaptureInterval(item, range);
            if (bounds.From >= bounds.To) continue;
            var intervals = includeTags.Length == 0 ? new List<(int From, int To)> { bounds }
                : included.Select(x => ClipCaptureInterval(item, x))
                    .Select(x => (From: Math.Max(x.From, bounds.From), To: Math.Min(x.To, bounds.To)))
                    .Where(x => x.From < x.To).OrderBy(x => x.From).ToList();
            var merged = new List<(int From, int To)>();
            foreach (var interval in intervals)
            {
                if (merged.Count > 0 && interval.From <= merged[^1].To)
                    merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, interval.To));
                else merged.Add(interval);
            }
            foreach (var exclusion in excluded.Select(x => ClipCaptureInterval(item, x)).Where(x => x.From < x.To))
            {
                var remaining = new List<(int From, int To)>();
                foreach (var interval in merged)
                {
                    if (exclusion.To <= interval.From || exclusion.From >= interval.To) remaining.Add(interval);
                    else
                    {
                        if (interval.From < exclusion.From) remaining.Add((interval.From, exclusion.From));
                        if (exclusion.To < interval.To) remaining.Add((exclusion.To, interval.To));
                    }
                }
                merged = remaining;
            }
            foreach (var interval in merged)
            {
                var segment = item with { Start = interval.From, Length = interval.To - interval.From };
                if (textSearch is null || segment.Text.Contains(textSearch, StringComparison.OrdinalIgnoreCase)) items.Add(segment);
            }
        }
        return items;
    }

    private static (int From, int To) ClipCaptureInterval(CaptureTextItem item, CaptureRange? range)
    {
        if (range is null) return (item.Start, item.Start + item.Length);
        if (item.EndPosition.CompareTo(range.Start) <= 0 || item.StartPosition.CompareTo(range.End) >= 0) return (0, 0);
        return (item.SessionId == range.Start.SessionId && item.Sequence == range.Start.Sequence ? Math.Max(item.Start, range.Start.Offset) : item.Start,
            item.SessionId == range.End.SessionId && item.Sequence == range.End.Sequence ? Math.Min(item.Start + item.Length, range.End.Offset) : item.Start + item.Length);
    }

    public async Task<IReadOnlyList<CaptureTextItem>> GetGameCaptureItemsAsync(long profileId, int limit = 200, int offset = 0,
        long? labelId = null, bool unclassifiedOnly = false, bool latest = false, long? rangeId = null, CaptureKind? kind = null,
        string? textSearch = null, string? speakerSearch = null)
    {
        if (limit < 0 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var range = rangeId is null ? null : (await GetCaptureRangesAsync(profileId)).SingleOrDefault(x => x.Id == rangeId)
            ?? throw new ArgumentException("このゲームの区間を選んでください。");
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        var order = latest ? " DESC" : "";
        textSearch = string.IsNullOrWhiteSpace(textSearch) ? null : textSearch.Trim();
        speakerSearch = string.IsNullOrWhiteSpace(speakerSearch) ? null : speakerSearch.Trim();
        var searching = textSearch is not null || speakerSearch is not null;
        await using var command = CaptureCommand(connection, null, CaptureItemQuery + """
             JOIN CaptureSessions s ON s.Id=i.SessionId
             WHERE s.CaptureGameId=$game AND ($label IS NULL OR l.Id=$label) AND ($unclassified=0 OR l.Id IS NULL)
             AND ($kind IS NULL OR l.Kind=$kind)
             AND ($range=0 OR ((i.SessionId,i.ReceiveSequence,i.StartUtf16+i.LengthUtf16)>($ss,$sq,$so)
                 AND (i.SessionId,i.ReceiveSequence,i.StartUtf16)<($es,$eq,$eo)))
            """ + $" ORDER BY i.SessionId{order},i.ReceiveSequence{order},i.StartUtf16{order} LIMIT $limit OFFSET $offset",
            ("$game", profileId), ("$label", labelId), ("$unclassified", unclassifiedOnly), ("$kind", kind is null ? null : (int)kind), ("$range", range is null ? 0 : 1),
            ("$ss", range?.Start.SessionId ?? 0), ("$sq", range?.Start.Sequence ?? 0), ("$so", range?.Start.Offset ?? 0),
            ("$es", range?.End.SessionId ?? 0), ("$eq", range?.End.Sequence ?? 0), ("$eo", range?.End.Offset ?? 0),
            ("$limit", searching || limit == 0 ? -1 : limit), ("$offset", searching ? 0 : offset));
        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<CaptureTextItem>();
        var skipped = 0;
        while (await reader.ReadAsync())
        {
            var item = ReadCaptureItem(reader);
            if (range is not null)
            {
                var from = item.SessionId == range.Start.SessionId && item.Sequence == range.Start.Sequence ? Math.Max(item.Start, range.Start.Offset) : item.Start;
                var to = item.SessionId == range.End.SessionId && item.Sequence == range.End.Sequence ? Math.Min(item.Start + item.Length, range.End.Offset) : item.Start + item.Length;
                item = item with { Start = from, Length = to - from };
            }
            if (searching)
            {
                // Search the displayed UTF-16 slice and effective speaker before paging.
                if (textSearch is not null && !item.Text.Contains(textSearch, StringComparison.OrdinalIgnoreCase)) continue;
                if (speakerSearch is not null && item.Speaker?.Contains(speakerSearch, StringComparison.OrdinalIgnoreCase) != true) continue;
                if (skipped++ < offset) continue;
            }
            items.Add(item);
            if (searching && limit > 0 && items.Count == limit) break;
        }
        if (latest) items.Reverse();
        return items;
    }
}
