using GameLauncherWithGit.Application.Models;
using System.Globalization;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    public async Task<IReadOnlyList<CaptureRange>> GetCaptureRangesAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null, """
            SELECT Id,Name,StartSessionId,StartSequence,StartOffset,EndSessionId,EndSequence,EndOffset
            FROM CaptureRanges WHERE CaptureGameId=$game ORDER BY StartSessionId,StartSequence,StartOffset,Id
            """, ("$game", profileId));
        await using var reader = await command.ExecuteReaderAsync();
        var ranges = new List<CaptureRange>();
        while (await reader.ReadAsync()) ranges.Add(new(reader.GetInt64(0), profileId, reader.GetString(1),
            new(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4)), new(reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7))));
        return ranges;
    }

    public async Task<long> SaveCaptureRangeAsync(long profileId, string name, CapturePosition start, CapturePosition end)
    {
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
        await transaction.CommitAsync();
        return id;
    }

    public async Task<IReadOnlyList<CaptureTextItem>> GetGameCaptureItemsAsync(long profileId, int limit = 200, int offset = 0,
        long? labelId = null, bool unclassifiedOnly = false, bool latest = false, long? rangeId = null, CaptureKind? kind = null)
    {
        if (limit < 0 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var range = rangeId is null ? null : (await GetCaptureRangesAsync(profileId)).SingleOrDefault(x => x.Id == rangeId)
            ?? throw new ArgumentException("このゲームの区間を選んでください。");
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        var order = latest ? " DESC" : "";
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
            ("$limit", limit == 0 ? -1 : limit), ("$offset", offset));
        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<CaptureTextItem>();
        while (await reader.ReadAsync())
        {
            var item = ReadCaptureItem(reader);
            if (range is not null)
            {
                var from = item.SessionId == range.Start.SessionId && item.Sequence == range.Start.Sequence ? Math.Max(item.Start, range.Start.Offset) : item.Start;
                var to = item.SessionId == range.End.SessionId && item.Sequence == range.End.Sequence ? Math.Min(item.Start + item.Length, range.End.Offset) : item.Start + item.Length;
                item = item with { Start = from, Length = to - from };
            }
            items.Add(item);
        }
        if (latest) items.Reverse();
        return items;
    }
}
