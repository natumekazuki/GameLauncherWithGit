using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    private static async Task EnsureCaptureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CaptureGames (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CurrentGameId TEXT NULL UNIQUE REFERENCES Games(Id) ON DELETE SET NULL,
                Title TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1,
                BodyExecutablePath TEXT NULL, SettingsJson TEXT NOT NULL DEFAULT '{}');
            CREATE TABLE IF NOT EXISTS CaptureSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id),
                StartedAt TEXT NOT NULL, EndedAt TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_CaptureSessions_Game ON CaptureSessions(CaptureGameId, StartedAt);
            CREATE TABLE IF NOT EXISTS CaptureSources (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL REFERENCES CaptureSessions(Id),
                SourceKey TEXT NOT NULL, ProcessId INTEGER NOT NULL,
                HookCode TEXT NOT NULL, HookName TEXT NOT NULL,
                Address TEXT NOT NULL, Context TEXT NOT NULL, Context2 TEXT NOT NULL,
                UNIQUE(SessionId, SourceKey), UNIQUE(SessionId, Id));
            CREATE TABLE IF NOT EXISTS CaptureEvents (
                SessionId INTEGER NOT NULL REFERENCES CaptureSessions(Id),
                ReceiveSequence INTEGER NOT NULL, SourceId INTEGER NOT NULL,
                ReceivedAt TEXT NOT NULL, InputRole INTEGER NOT NULL, RawText TEXT NOT NULL,
                HasManualBoundaries INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(SessionId, ReceiveSequence),
                FOREIGN KEY(SessionId, SourceId) REFERENCES CaptureSources(SessionId, Id));
            CREATE INDEX IF NOT EXISTS IX_CaptureEvents_Time ON CaptureEvents(SessionId, ReceivedAt);
            CREATE TABLE IF NOT EXISTS CaptureLabels (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id),
                Name TEXT NOT NULL, Kind INTEGER NOT NULL,
                UNIQUE(CaptureGameId, Name));
            CREATE TABLE IF NOT EXISTS CaptureItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId INTEGER NOT NULL, ReceiveSequence INTEGER NOT NULL,
                StartUtf16 INTEGER NOT NULL CHECK(StartUtf16 >= 0),
                LengthUtf16 INTEGER NOT NULL CHECK(LengthUtf16 > 0),
                AutoLabelId INTEGER NULL REFERENCES CaptureLabels(Id),
                HasManualLabel INTEGER NOT NULL DEFAULT 0,
                ManualLabelId INTEGER NULL REFERENCES CaptureLabels(Id),
                AutoSpeakerText TEXT NULL, HasManualSpeaker INTEGER NOT NULL DEFAULT 0,
                ManualSpeakerText TEXT NULL,
                FOREIGN KEY(SessionId, ReceiveSequence) REFERENCES CaptureEvents(SessionId, ReceiveSequence),
                UNIQUE(SessionId, ReceiveSequence, StartUtf16));
            CREATE TABLE IF NOT EXISTS CaptureRanges (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id), Name TEXT NOT NULL,
                StartSessionId INTEGER NOT NULL REFERENCES CaptureSessions(Id), StartSequence INTEGER NOT NULL, StartOffset INTEGER NOT NULL,
                EndSessionId INTEGER NOT NULL REFERENCES CaptureSessions(Id), EndSequence INTEGER NOT NULL, EndOffset INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_CaptureRanges_Game ON CaptureRanges(CaptureGameId,Id);
            CREATE TABLE IF NOT EXISTS CaptureImages (
                Id TEXT PRIMARY KEY, PixelHash TEXT NOT NULL UNIQUE,
                Width INTEGER NOT NULL, Height INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS CaptureEventImages (
                SessionId INTEGER NOT NULL, ReceiveSequence INTEGER NOT NULL,
                ImageId TEXT NOT NULL REFERENCES CaptureImages(Id), CapturedAt TEXT NOT NULL,
                PRIMARY KEY(SessionId,ReceiveSequence,ImageId),
                FOREIGN KEY(SessionId,ReceiveSequence) REFERENCES CaptureEvents(SessionId,ReceiveSequence) ON DELETE CASCADE);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CaptureCommand(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public async Task<CaptureProfile> GetOrCreateCaptureProfileAsync(string gameId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        await using var insert = CaptureCommand(connection, transaction, """
            INSERT INTO CaptureGames(CurrentGameId,Title)
            SELECT Id,Title FROM Games WHERE Id=$game
            ON CONFLICT(CurrentGameId) DO NOTHING;
            """, ("$game", gameId));
        var created = await insert.ExecuteNonQueryAsync() == 1;
        await using var query = CaptureCommand(connection, transaction,
            "SELECT Id,CurrentGameId,Title,Enabled,BodyExecutablePath,SettingsJson FROM CaptureGames WHERE CurrentGameId=$game", ("$game", gameId));
        CaptureProfile profile;
        await using (var reader = await query.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidOperationException("ゲーム登録が見つかりません。");
            profile = ReadCaptureProfile(reader);
        }
        if (created)
        {
            foreach (var (name, kind) in new[] { ("セリフ", CaptureKind.Dialogue), ("地の文", CaptureKind.Narration) })
            {
                await using var label = CaptureCommand(connection, transaction,
                    "INSERT INTO CaptureLabels(CaptureGameId,Name,Kind) VALUES($game,$name,$kind)",
                    ("$game", profile.Id), ("$name", name), ("$kind", (int)kind));
                await label.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
        return profile;
    }

    private static CaptureProfile ReadCaptureProfile(SqliteDataReader reader) => new(reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        JsonSerializer.Deserialize<CaptureRules>(reader.GetString(5), JsonOptions) ?? new());

    public async Task<IReadOnlyList<CaptureProfile>> GetCaptureProfilesAsync()
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null,
            "SELECT Id,CurrentGameId,Title,Enabled,BodyExecutablePath,SettingsJson FROM CaptureGames ORDER BY Title,Id");
        var result = new List<CaptureProfile>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(ReadCaptureProfile(reader));
        return result;
    }

    public async Task SaveCaptureProfileAsync(CaptureProfile profile)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null, """
            UPDATE CaptureGames SET Enabled=$enabled,BodyExecutablePath=$path,SettingsJson=$rules WHERE Id=$id
            """, ("$enabled", profile.Enabled), ("$path", profile.BodyExecutablePath),
            ("$rules", JsonSerializer.Serialize(profile.Rules, JsonOptions)), ("$id", profile.Id));
        if (await command.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("抽出設定が見つかりません。");
    }

    public async Task<IReadOnlyList<CaptureLabel>> GetCaptureLabelsAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        return await ReadCaptureLabelsAsync(connection, null, profileId);
    }

    private static async Task<List<CaptureLabel>> ReadCaptureLabelsAsync(SqliteConnection connection, SqliteTransaction? transaction, long profileId)
    {
        await using var command = CaptureCommand(connection, transaction,
            "SELECT Id,Name,Kind FROM CaptureLabels WHERE CaptureGameId=$id ORDER BY Id", ("$id", profileId));
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<CaptureLabel>();
        while (await reader.ReadAsync()) result.Add(new(reader.GetInt64(0), profileId, reader.GetString(1), (CaptureKind)reader.GetInt32(2)));
        return result;
    }

    public async Task<long> BeginCaptureSessionAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null,
            "INSERT INTO CaptureSessions(CaptureGameId,StartedAt) VALUES($id,$time) RETURNING Id",
            ("$id", profileId), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task EndCaptureSessionAsync(long sessionId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null,
            "UPDATE CaptureSessions SET EndedAt=$time WHERE Id=$id", ("$id", sessionId), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<CaptureSession>> GetCaptureSessionsAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null,
            "SELECT Id,StartedAt,EndedAt FROM CaptureSessions WHERE CaptureGameId=$id ORDER BY StartedAt DESC,Id DESC", ("$id", profileId));
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<CaptureSession>();
        while (await reader.ReadAsync()) result.Add(new(reader.GetInt64(0), profileId,
            DateTimeOffset.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2))));
        return result;
    }

    private static async Task<long> CaptureOwnerAsync(SqliteConnection connection, SqliteTransaction? transaction, long sessionId)
    {
        await using var command = CaptureCommand(connection, transaction,
            "SELECT CaptureGameId FROM CaptureSessions WHERE Id=$id", ("$id", sessionId));
        return await command.ExecuteScalarAsync() is long id ? id : throw new InvalidOperationException("プレイ回が見つかりません。");
    }

    private static void ValidateCaptureLabel(IReadOnlyList<CaptureLabel> labels, long? labelId)
    {
        if (labelId is not null && !labels.Any(x => x.Id == labelId)) throw new ArgumentException("対象ゲームのラベルを選んでください。");
    }

    private static async Task InsertCaptureItemsAsync(SqliteConnection connection, SqliteTransaction transaction,
        long sessionId, CaptureReceived received, CaptureRules rules, IReadOnlyList<CaptureLabel> labels)
    {
        if (received.Role == CaptureRole.Name || received.Text.Length == 0) return;
        ValidateCaptureLabel(labels, received.LabelId);
        foreach (var segment in ScenarioTextProcessor.Split(received.Text, rules))
        {
            var labelId = received.LabelId ?? labels.FirstOrDefault(x => x.Kind == segment.Kind)?.Id;
            await using var command = CaptureCommand(connection, transaction, """
                INSERT INTO CaptureItems(SessionId,ReceiveSequence,StartUtf16,LengthUtf16,AutoLabelId)
                VALUES($session,$sequence,$start,$length,$label)
                """, ("$session", sessionId), ("$sequence", received.Sequence),
                ("$start", segment.Start), ("$length", segment.Length), ("$label", labelId));
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task<bool> AppendCaptureAsync(long sessionId, CaptureReceived received, CaptureRules rules)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        var owner = await CaptureOwnerAsync(connection, transaction, sessionId);
        var labels = await ReadCaptureLabelsAsync(connection, transaction, owner);
        var source = received.Source;
        await using var addSource = CaptureCommand(connection, transaction, """
            INSERT INTO CaptureSources(SessionId,SourceKey,ProcessId,HookCode,HookName,Address,Context,Context2)
            VALUES($session,$key,$pid,$code,$name,$address,$context,$context2)
            ON CONFLICT(SessionId,SourceKey) DO NOTHING;
            SELECT Id FROM CaptureSources WHERE SessionId=$session AND SourceKey=$key;
            """, ("$session", sessionId), ("$key", source.Key), ("$pid", source.ProcessId),
            ("$code", source.HookCode), ("$name", source.HookName), ("$address", source.Address.ToString("x")),
            ("$context", source.Context.ToString("x")), ("$context2", source.Context2.ToString("x")));
        var sourceId = await addSource.ExecuteScalarAsync();
        await using var addEvent = CaptureCommand(connection, transaction, """
            INSERT INTO CaptureEvents(SessionId,ReceiveSequence,SourceId,ReceivedAt,InputRole,RawText)
            VALUES($session,$sequence,$source,$time,$role,$text) ON CONFLICT(SessionId,ReceiveSequence) DO NOTHING
            """, ("$session", sessionId), ("$sequence", received.Sequence), ("$source", sourceId),
            ("$time", received.ReceivedAt.ToUniversalTime().ToString("O")), ("$role", (int)received.Role), ("$text", received.Text));
        var inserted = await addEvent.ExecuteNonQueryAsync() == 1;
        if (inserted) await InsertCaptureItemsAsync(connection, transaction, sessionId, received, rules, labels);
        await transaction.CommitAsync();
        return inserted;
    }

    private const string CaptureItemQuery = """
        SELECT i.Id,i.SessionId,i.ReceiveSequence,e.ReceivedAt,e.RawText,i.StartUtf16,i.LengthUtf16,
        l.Id,l.Name,l.Kind,CASE WHEN l.Kind=1 THEN NULL WHEN i.HasManualSpeaker=1 THEN i.ManualSpeakerText ELSE i.AutoSpeakerText END,
        i.HasManualLabel,i.HasManualSpeaker,e.HasManualBoundaries
        FROM CaptureItems i JOIN CaptureEvents e ON e.SessionId=i.SessionId AND e.ReceiveSequence=i.ReceiveSequence
        LEFT JOIN CaptureLabels l ON l.Id=CASE WHEN i.HasManualLabel=1 THEN i.ManualLabelId ELSE i.AutoLabelId END
        """;

    private static CaptureTextItem ReadCaptureItem(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2),
        DateTimeOffset.Parse(r.GetString(3)), r.GetString(4), r.GetInt32(5), r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : (CaptureKind)r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10),
        r.GetBoolean(11), r.GetBoolean(12), r.GetBoolean(13));

    public async Task<IReadOnlyList<CaptureTextItem>> GetCaptureItemsAsync(long sessionId, int limit = 200,
        int offset = 0, long? labelId = null, bool unclassifiedOnly = false, bool latest = false)
    {
        if (limit < 0 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        var order = latest ? " ORDER BY i.ReceiveSequence DESC,i.StartUtf16 DESC" : " ORDER BY i.ReceiveSequence,i.StartUtf16";
        await using var command = CaptureCommand(connection, null, CaptureItemQuery + """
             WHERE i.SessionId=$session AND ($label IS NULL OR l.Id=$label) AND ($unclassified=0 OR l.Id IS NULL)
            """ + order + " LIMIT $limit OFFSET $offset", ("$session", sessionId), ("$label", labelId), ("$unclassified", unclassifiedOnly),
            ("$limit", limit == 0 ? -1 : limit), ("$offset", offset));
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<CaptureTextItem>();
        while (await reader.ReadAsync()) result.Add(ReadCaptureItem(reader));
        if (latest) result.Reverse();
        return result;
    }

    public async Task SetCaptureLabelsAsync(long sessionId, IReadOnlyCollection<long> itemIds, long? labelId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        ValidateCaptureLabel(await ReadCaptureLabelsAsync(connection, transaction,
            await CaptureOwnerAsync(connection, transaction, sessionId)), labelId);
        foreach (var id in itemIds.Distinct())
        {
            await using var command = CaptureCommand(connection, transaction,
                "UPDATE CaptureItems SET HasManualLabel=1,ManualLabelId=$label WHERE Id=$id AND SessionId=$session",
                ("$id", id), ("$session", sessionId), ("$label", labelId));
            if (await command.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("修正対象が見つかりません。");
        }
        await transaction.CommitAsync();
    }

    public async Task SetCaptureSpeakerAsync(long sessionId, long itemId, string? speaker)
    {
        speaker = string.IsNullOrWhiteSpace(speaker) ? null : speaker.Trim();
        if (speaker?.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("話者名は1行で入力してください。");
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = CaptureCommand(connection, null,
            "UPDATE CaptureItems SET HasManualSpeaker=1,ManualSpeakerText=$speaker WHERE Id=$id AND SessionId=$session",
            ("$id", itemId), ("$session", sessionId), ("$speaker", speaker));
        if (await command.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("修正対象が見つかりません。");
    }

    public async Task SplitCaptureEventAsync(long sessionId, long sequence, IReadOnlyList<int> boundaries, IReadOnlyList<CaptureManualPart>? parts = null)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        await using var find = CaptureCommand(connection, transaction,
            "SELECT RawText FROM CaptureEvents WHERE SessionId=$session AND ReceiveSequence=$sequence AND InputRole<>2",
            ("$session", sessionId), ("$sequence", sequence));
        var raw = await find.ExecuteScalarAsync() as string ?? throw new InvalidOperationException("本文原文が見つかりません。");
        ScenarioTextProcessor.ValidateBoundaries(raw, boundaries);
        if (parts is not null)
        {
            if (parts.Count != boundaries.Count + 1) throw new ArgumentException("分割数が一致しません。");
            var labels = await ReadCaptureLabelsAsync(connection, transaction, await CaptureOwnerAsync(connection, transaction, sessionId));
            foreach (var part in parts)
            {
                ValidateCaptureLabel(labels, part.LabelId);
                if (part.Speaker?.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("話者名は1行で入力してください。");
            }
        }
        await using var clear = CaptureCommand(connection, transaction,
            "DELETE FROM CaptureItems WHERE SessionId=$session AND ReceiveSequence=$sequence",
            ("$session", sessionId), ("$sequence", sequence));
        await clear.ExecuteNonQueryAsync();
        var start = 0;
        var index = 0;
        foreach (var end in boundaries.Append(raw.Length))
        {
            if (end > start)
            {
                await using var add = CaptureCommand(connection, transaction, """
                    INSERT INTO CaptureItems(SessionId,ReceiveSequence,StartUtf16,LengthUtf16,HasManualLabel,ManualLabelId,HasManualSpeaker,ManualSpeakerText)
                    VALUES($session,$sequence,$start,$length,$manual,$label,$manual,$speaker)
                    """, ("$session", sessionId), ("$sequence", sequence), ("$start", start), ("$length", end - start),
                    ("$manual", parts is not null), ("$label", parts?[index].LabelId), ("$speaker", parts?[index].Speaker));
                await add.ExecuteNonQueryAsync();
            }
            start = end;
            index++;
        }
        await using var mark = CaptureCommand(connection, transaction,
            "UPDATE CaptureEvents SET HasManualBoundaries=1 WHERE SessionId=$session AND ReceiveSequence=$sequence",
            ("$session", sessionId), ("$sequence", sequence));
        await mark.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task ReapplyCaptureRulesAsync(long sessionId, IReadOnlyCollection<long> itemIds, CaptureRules rules)
    {
        var all = await GetCaptureItemsAsync(sessionId, 0);
        var selected = itemIds.ToHashSet();
        var changed = new HashSet<long>();
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        var labels = await ReadCaptureLabelsAsync(connection, transaction, await CaptureOwnerAsync(connection, transaction, sessionId));
        foreach (var group in all.GroupBy(x => x.Sequence))
        {
            var target = group.Where(x => selected.Contains(x.Id)).ToArray();
            if (target.Length == 0) continue;
            await using var sourceQuery = CaptureCommand(connection, transaction, """
                SELECT s.ProcessId,s.Address,s.Context,s.Context2,s.HookCode,s.HookName
                FROM CaptureEvents e JOIN CaptureSources s ON s.Id=e.SourceId
                WHERE e.SessionId=$session AND e.ReceiveSequence=$sequence
                """, ("$session", sessionId), ("$sequence", group.Key));
            CaptureSource source;
            await using (var reader = await sourceQuery.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync()) continue;
                source = new((uint)reader.GetInt64(0), ulong.Parse(reader.GetString(1), NumberStyles.HexNumber),
                    ulong.Parse(reader.GetString(2), NumberStyles.HexNumber), ulong.Parse(reader.GetString(3), NumberStyles.HexNumber),
                    reader.GetString(4), reader.GetString(5));
            }
            var matches = rules.Selections.Where(x => x.Matches(source)).ToArray();
            var sourceLabel = matches.Length == 1 ? matches[0].LabelId : null;
            ValidateCaptureLabel(labels, sourceLabel);
            if (target.Length == group.Count() && !group.Any(x => x.ManualBoundaries || x.ManualLabel || x.ManualSpeaker))
            {
                await using var clear = CaptureCommand(connection, transaction,
                    "DELETE FROM CaptureItems WHERE SessionId=$session AND ReceiveSequence=$sequence",
                    ("$session", sessionId), ("$sequence", group.Key));
                await clear.ExecuteNonQueryAsync();
                await InsertCaptureItemsAsync(connection, transaction, sessionId,
                    new(group.Key, target[0].ReceivedAt, source, target[0].RawText, CaptureRole.Body, sourceLabel), rules, labels);
                await using var ids = CaptureCommand(connection, transaction,
                    "SELECT Id FROM CaptureItems WHERE SessionId=$session AND ReceiveSequence=$sequence",
                    ("$session", sessionId), ("$sequence", group.Key));
                await using var reader = await ids.ExecuteReaderAsync();
                while (await reader.ReadAsync()) changed.Add(reader.GetInt64(0));
            }
            else foreach (var item in target)
            {
                var segments = ScenarioTextProcessor.Split(item.Text, rules);
                var label = sourceLabel ?? (segments.Count == 1 ? labels.FirstOrDefault(x => x.Kind == segments[0].Kind)?.Id : null);
                await using var update = CaptureCommand(connection, transaction,
                    "UPDATE CaptureItems SET AutoLabelId=$label WHERE Id=$id AND SessionId=$session",
                    ("$label", label), ("$id", item.Id), ("$session", sessionId));
                await update.ExecuteNonQueryAsync();
                changed.Add(item.Id);
            }
        }
        await transaction.CommitAsync();
        var times = all.Where(x => selected.Contains(x.Id)).Select(x => x.ReceivedAt).ToArray();
        if (times.Length > 0) await ResolveCaptureSpeakersCoreAsync(sessionId, times.Min(),
            DateTimeOffset.UtcNow > times.Max().AddSeconds(2) ? DateTimeOffset.UtcNow : times.Max().AddSeconds(2), changed);
    }

    public Task ResolveCaptureSpeakersAsync(long sessionId, DateTimeOffset since, DateTimeOffset now,
        IReadOnlyCollection<long>? sequences = null) => ResolveCaptureSpeakersCoreAsync(sessionId, since, now, null, sequences?.ToHashSet());

    private async Task ResolveCaptureSpeakersCoreAsync(long sessionId, DateTimeOffset since,
        DateTimeOffset now, IReadOnlySet<long>? selected, IReadOnlySet<long>? sequences = null)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        var events = new List<CaptureReceived>();
        await using (var query = CaptureCommand(connection, null, """
            SELECT e.ReceiveSequence,e.ReceivedAt,e.RawText,e.InputRole,s.ProcessId,s.Address,s.Context,s.Context2,s.HookCode,s.HookName
            FROM CaptureEvents e JOIN CaptureSources s ON s.Id=e.SourceId
            WHERE e.SessionId=$session AND e.ReceivedAt >= $since ORDER BY e.ReceiveSequence
            """, ("$session", sessionId), ("$since", since.AddSeconds(-2).ToUniversalTime().ToString("O"))))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) events.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)),
                new((uint)reader.GetInt64(4), ulong.Parse(reader.GetString(5), NumberStyles.HexNumber),
                    ulong.Parse(reader.GetString(6), NumberStyles.HexNumber), ulong.Parse(reader.GetString(7), NumberStyles.HexNumber),
                    reader.GetString(8), reader.GetString(9)), reader.GetString(2), (CaptureRole)reader.GetInt32(3)));
        var items = new List<CaptureTextItem>();
        await using (var query = CaptureCommand(connection, null, CaptureItemQuery +
            " WHERE i.SessionId=$session AND e.ReceivedAt >= $since ORDER BY i.ReceiveSequence,i.StartUtf16",
            ("$session", sessionId), ("$since", since.AddSeconds(-2).ToUniversalTime().ToString("O"))))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) items.Add(ReadCaptureItem(reader));
        await using var transaction = connection.BeginTransaction();
        foreach (var process in events.GroupBy(x => x.Source.ProcessId))
        {
            var processSequences = process.Select(x => x.Sequence).ToHashSet();
            foreach (var (id, speaker) in ScenarioTextProcessor.MatchSpeakers(process.ToArray(),
                items.Where(x => processSequences.Contains(x.Sequence)).ToArray(), now))
            {
                if (selected is not null && !selected.Contains(id)) continue;
                if (sequences is not null && !items.Any(x => x.Id == id && sequences.Contains(x.Sequence))) continue;
                await using var update = CaptureCommand(connection, transaction,
                    "UPDATE CaptureItems SET AutoSpeakerText=$speaker WHERE Id=$id", ("$id", id), ("$speaker", speaker));
                await update.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }
}
