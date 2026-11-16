using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLauncherWithGit.Application.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    private readonly SemaphoreSlim _scenarioMatchingGate = new(1, 1);
    private readonly object _scenarioMatchingLifetime = new();
    private readonly HashSet<long> _scenarioMatchingQueue = [];
    private readonly CancellationTokenSource _scenarioMatchingStop = new();
    private Task? _scenarioMatchingTask;
    private bool _scenarioMatchingStarted;
    private volatile bool _scenarioMatchingDisposed;
    private int _captureWritesPending;

    private static async Task EnsureScenarioMatchingSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CaptureCommand(connection, null, """
            CREATE TABLE IF NOT EXISTS ScenarioMaterialState (
                CaptureGameId INTEGER PRIMARY KEY REFERENCES CaptureGames(Id),
                Revision INTEGER NOT NULL DEFAULT 0, MatchingRevision INTEGER NOT NULL DEFAULT 0,
                Dirty INTEGER NOT NULL DEFAULT 1,
                CursorSession INTEGER NOT NULL DEFAULT 0, CursorSequence INTEGER NOT NULL DEFAULT 0,
                CursorStart INTEGER NOT NULL DEFAULT -1);
            INSERT OR IGNORE INTO ScenarioMaterialState(CaptureGameId) SELECT Id FROM CaptureGames;
            CREATE TABLE IF NOT EXISTS ScenarioSequenceIndex (
                ItemId INTEGER PRIMARY KEY REFERENCES CaptureItems(Id) ON DELETE CASCADE,
                CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id),
                SessionId INTEGER NOT NULL, Ordinal INTEGER NOT NULL,
                CanonicalItemId INTEGER NOT NULL, GroupSpeaker TEXT NULL, SeedHash TEXT NULL,
                UNIQUE(SessionId,Ordinal));
            CREATE INDEX IF NOT EXISTS IX_ScenarioSequenceIndex_Seed ON ScenarioSequenceIndex(CaptureGameId,SeedHash);
            CREATE INDEX IF NOT EXISTS IX_ScenarioSequenceIndex_Canonical ON ScenarioSequenceIndex(CanonicalItemId);
            CREATE TABLE IF NOT EXISTS ScenarioMatchRanges (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id),
                RepresentativeSession INTEGER NOT NULL, RepresentativeStart INTEGER NOT NULL,
                RevisitSession INTEGER NOT NULL, RevisitStart INTEGER NOT NULL, ItemCount INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_ScenarioMatchRanges_Game ON ScenarioMatchRanges(CaptureGameId);
            CREATE TABLE IF NOT EXISTS ScenarioMatchProgress (
                SessionId INTEGER PRIMARY KEY REFERENCES CaptureSessions(Id) ON DELETE CASCADE,
                CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id), StateJson TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS ScenarioMaterial_NewGame AFTER INSERT ON CaptureGames BEGIN
                INSERT INTO ScenarioMaterialState(CaptureGameId) VALUES(NEW.Id);
            END;
            CREATE TRIGGER IF NOT EXISTS ScenarioMaterial_ItemInsert AFTER INSERT ON CaptureItems BEGIN
                UPDATE ScenarioMaterialState SET Revision=Revision+1,
                    Dirty=CASE WHEN (NEW.SessionId,NEW.ReceiveSequence,NEW.StartUtf16)<=(CursorSession,CursorSequence,CursorStart)
                        THEN 1 ELSE Dirty END
                WHERE CaptureGameId=(SELECT CaptureGameId FROM CaptureSessions WHERE Id=NEW.SessionId);
            END;
            CREATE TRIGGER IF NOT EXISTS ScenarioMaterial_ItemUpdate AFTER UPDATE ON CaptureItems
            WHEN OLD.StartUtf16 IS NOT NEW.StartUtf16 OR OLD.LengthUtf16 IS NOT NEW.LengthUtf16
                OR OLD.AutoLabelId IS NOT NEW.AutoLabelId OR OLD.HasManualLabel IS NOT NEW.HasManualLabel
                OR OLD.ManualLabelId IS NOT NEW.ManualLabelId OR OLD.AutoSpeakerText IS NOT NEW.AutoSpeakerText
                OR OLD.HasManualSpeaker IS NOT NEW.HasManualSpeaker OR OLD.ManualSpeakerText IS NOT NEW.ManualSpeakerText
            BEGIN
                UPDATE ScenarioMaterialState SET Revision=Revision+1,Dirty=1
                WHERE CaptureGameId=(SELECT CaptureGameId FROM CaptureSessions WHERE Id=NEW.SessionId);
            END;
            CREATE TRIGGER IF NOT EXISTS ScenarioMaterial_ItemDelete BEFORE DELETE ON CaptureItems BEGIN
                UPDATE ScenarioMaterialState SET Revision=Revision+1,Dirty=1
                WHERE CaptureGameId=(SELECT CaptureGameId FROM CaptureSessions WHERE Id=OLD.SessionId);
            END;
            CREATE TRIGGER IF NOT EXISTS ScenarioMaterial_EventUpdate AFTER UPDATE ON CaptureEvents
            WHEN OLD.RawText IS NOT NEW.RawText OR OLD.ReceivedAt IS NOT NEW.ReceivedAt
                OR OLD.InputRole IS NOT NEW.InputRole OR OLD.HasManualBoundaries IS NOT NEW.HasManualBoundaries
            BEGIN
                UPDATE ScenarioMaterialState SET Revision=Revision+1,Dirty=1
                WHERE CaptureGameId=(SELECT CaptureGameId FROM CaptureSessions WHERE Id=NEW.SessionId);
            END;
            CREATE TRIGGER IF NOT EXISTS ScenarioMaterial_GameUpdate AFTER UPDATE ON CaptureGames
            WHEN OLD.Title IS NOT NEW.Title OR OLD.CurrentGameId IS NOT NEW.CurrentGameId BEGIN
                UPDATE ScenarioMaterialState SET Revision=Revision+1 WHERE CaptureGameId=NEW.Id;
            END;
            CREATE TRIGGER IF NOT EXISTS ScenarioMaterial_LabelUpdate AFTER UPDATE ON CaptureLabels
            WHEN OLD.Name IS NOT NEW.Name OR OLD.Kind IS NOT NEW.Kind BEGIN
                UPDATE ScenarioMaterialState SET Revision=Revision+1,Dirty=1 WHERE CaptureGameId=NEW.CaptureGameId;
            END;
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
        // These are material metadata, not inputs to sequence matching.
        foreach (var (table, owner) in new[] {
            ("CaptureRanges", "{row}.CaptureGameId"),
            ("CaptureRangeTags", "(SELECT CaptureGameId FROM CaptureRanges WHERE Id={row}.RangeId)"),
            ("CaptureEventImages", "(SELECT CaptureGameId FROM CaptureSessions WHERE Id={row}.SessionId)") })
        {
            foreach (var action in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var row = action == "DELETE" ? "OLD" : "NEW";
                var timing = action == "DELETE" ? "BEFORE" : "AFTER";
                await using var trigger = CaptureCommand(connection, null, $"""
                    CREATE TRIGGER IF NOT EXISTS ScenarioMaterial_{table}_{action} {timing} {action} ON {table} BEGIN
                        UPDATE ScenarioMaterialState SET Revision=Revision+1 WHERE CaptureGameId={owner.Replace("{row}", row)};
                    END;
                    """);
                await trigger.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    /// <summary>Use inside the transaction that changes flow or other material-only metadata.</summary>
    private static async Task BumpScenarioMaterialRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, long captureGameId)
    {
        await using var command = CaptureCommand(connection, transaction,
            "UPDATE ScenarioMaterialState SET Revision=Revision+1 WHERE CaptureGameId=$game", ("$game", captureGameId));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Starts the application-owned worker. Dispose/DisposeAsync cancels and joins it.</summary>
    public void StartScenarioMatching()
    {
        lock (_scenarioMatchingLifetime)
        {
            ObjectDisposedException.ThrowIf(_scenarioMatchingDisposed, this);
            if (_scenarioMatchingStop.IsCancellationRequested) throw new InvalidOperationException("照合workerは終了しています。");
            if (_scenarioMatchingStarted) return;
            _scenarioMatchingStarted = true;
            _scenarioMatchingTask = Task.Run(() => RunScenarioMatchingAsync(initialize: true));
        }
    }

    private void ScheduleScenarioMatching(long captureGameId)
    {
        lock (_scenarioMatchingLifetime)
        {
            if (!_scenarioMatchingStarted || _scenarioMatchingDisposed) return;
            _scenarioMatchingQueue.Add(captureGameId);
            _scenarioMatchingTask ??= Task.Run(() => RunScenarioMatchingAsync(initialize: false));
        }
    }

    private async Task RunScenarioMatchingAsync(bool initialize)
    {
        var token = _scenarioMatchingStop.Token;
        var completedNormally = false;
        try
        {
            if (initialize)
            {
                await EnsureInitializedAsync(token);
                await using var connection = await OpenConnectionAsync(token);
                await using var games = CaptureCommand(connection, null, "SELECT CaptureGameId FROM ScenarioMaterialState");
                await using var reader = await games.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    lock (_scenarioMatchingLifetime) _scenarioMatchingQueue.Add(reader.GetInt64(0));
                }
            }
            // Let the receiver's accepted writes acquire SQLite first.
            await Task.Delay(25, token);
            while (true)
            {
                long game;
                lock (_scenarioMatchingLifetime)
                {
                    token.ThrowIfCancellationRequested();
                    if (_scenarioMatchingQueue.Count == 0) { completedNormally = true; return; }
                    game = _scenarioMatchingQueue.First();
                    _scenarioMatchingQueue.Remove(game);
                }
                try
                {
                    var result = await ProcessScenarioMatchingBatchAsync(game, cancellationToken: token);
                    if (result.HasMore) lock (_scenarioMatchingLifetime) _scenarioMatchingQueue.Add(game);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogError(ex, "Scenario matching failed for {CaptureGameId}; capture data remains saved", game); }
                await Task.Delay(10, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_scenarioMatchingDisposed) { }
        catch (Exception ex) { _logger.LogError(ex, "Scenario matching startup failed; pending work remains persistent"); }
        finally
        {
            lock (_scenarioMatchingLifetime)
            {
                _scenarioMatchingTask = null;
                if (completedNormally && _scenarioMatchingStarted && !_scenarioMatchingDisposed && _scenarioMatchingQueue.Count > 0)
                    _scenarioMatchingTask = Task.Run(() => RunScenarioMatchingAsync(initialize: false));
            }
        }
    }

    private Task StopScenarioMatching()
    {
        lock (_scenarioMatchingLifetime)
        {
            _scenarioMatchingStarted = false;
            _scenarioMatchingStop.Cancel();
            return _scenarioMatchingTask ?? Task.CompletedTask;
        }
    }

    /// <summary>Joins only matching; an already accepted capture shutdown can still finish using the store.</summary>
    public Task StopScenarioMatchingAsync() => StopScenarioMatching();

    public void Dispose()
    {
        _scenarioMatchingDisposed = true;
        StopScenarioMatching().GetAwaiter().GetResult();
    }
    public async ValueTask DisposeAsync()
    {
        _scenarioMatchingDisposed = true;
        await StopScenarioMatching();
    }

    private sealed record MatchCandidate(long SessionId, int Start);
    private sealed class MatchProgress
    {
        public int SearchFloor { get; set; } = 1;
        public int TargetStart { get; set; }
        public int Length { get; set; }
        public long? ActiveRangeId { get; set; }
        public List<MatchCandidate> Candidates { get; set; } = [];
    }
    private sealed record IndexedText(CaptureTextItem Item, int Ordinal, long CanonicalId, string? GroupSpeaker);

    private static async Task<IndexedText?> ReadIndexedTextAsync(SqliteConnection connection, SqliteTransaction transaction, long session, int ordinal)
    {
        await using var command = CaptureCommand(connection, transaction,
            CaptureItemQuery.Replace("SELECT i.Id", "SELECT idx.Ordinal,idx.CanonicalItemId,root.GroupSpeaker,i.Id") +
            " JOIN ScenarioSequenceIndex idx ON idx.ItemId=i.Id JOIN ScenarioSequenceIndex root ON root.ItemId=idx.CanonicalItemId" +
            " WHERE idx.SessionId=$session AND idx.Ordinal=$ordinal", ("$session", session), ("$ordinal", ordinal));
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var item = new CaptureTextItem(reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            DateTimeOffset.Parse(reader.GetString(6)), reader.GetString(7), reader.GetInt32(8), reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : (CaptureKind)reader.GetInt32(12), reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetBoolean(14), reader.GetBoolean(15), reader.GetBoolean(16));
        return new(item, reader.GetInt32(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static bool Matchable(CaptureTextItem item) => item.Kind is CaptureKind.Dialogue or CaptureKind.Narration;
    private static bool SameText(IndexedText left, IndexedText right) => Matchable(left.Item) && Matchable(right.Item)
        && left.Item.Text == right.Item.Text && left.Item.Kind == right.Item.Kind && left.Item.LabelId == right.Item.LabelId
        && (string.IsNullOrWhiteSpace(left.Item.Speaker) || string.IsNullOrWhiteSpace(right.Item.Speaker) || left.Item.Speaker == right.Item.Speaker)
        && (string.IsNullOrWhiteSpace(left.Item.Speaker) || string.IsNullOrWhiteSpace(right.GroupSpeaker) || left.Item.Speaker == right.GroupSpeaker)
        && (string.IsNullOrWhiteSpace(right.Item.Speaker) || string.IsNullOrWhiteSpace(left.GroupSpeaker) || right.Item.Speaker == left.GroupSpeaker)
        && (string.IsNullOrWhiteSpace(left.GroupSpeaker) || string.IsNullOrWhiteSpace(right.GroupSpeaker) || left.GroupSpeaker == right.GroupSpeaker);

    private static string SeedHash(IEnumerable<IndexedText> items) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(items.Select(x => new { x.Item.Text, x.Item.Kind, x.Item.LabelId })))));

    public async Task<ScenarioMatchingBatchResult> ProcessScenarioMatchingBatchAsync(long captureGameId, int batchSize = 32,
        CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await EnsureInitializedAsync(cancellationToken);
        if (Volatile.Read(ref _captureWritesPending) > 0) return new(0, true, true);
        await _scenarioMatchingGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            if (Volatile.Read(ref _captureWritesPending) > 0) return new(0, true, true);
            // The synchronous Sqlite provider runs here on a background thread, never on the receiver/UI thread.
            await using var transaction = connection.BeginTransaction();
            bool dirty;
            long session, sequence;
            int start;
            await using (var state = CaptureCommand(connection, transaction,
                "SELECT Dirty,CursorSession,CursorSequence,CursorStart FROM ScenarioMaterialState WHERE CaptureGameId=$game", ("$game", captureGameId)))
            await using (var reader = await state.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) throw new ArgumentException("ゲームの履歴が見つかりません。");
                dirty = reader.GetBoolean(0); session = reader.GetInt64(1); sequence = reader.GetInt64(2); start = reader.GetInt32(3);
            }
            if (dirty)
            {
                await using var reset = CaptureCommand(connection, transaction, """
                    DELETE FROM ScenarioSequenceIndex WHERE CaptureGameId=$game;
                    DELETE FROM ScenarioMatchRanges WHERE CaptureGameId=$game;
                    DELETE FROM ScenarioMatchProgress WHERE CaptureGameId=$game;
                    UPDATE ScenarioMaterialState SET Dirty=0,CursorSession=0,CursorSequence=0,CursorStart=-1 WHERE CaptureGameId=$game;
                    """, ("$game", captureGameId));
                await reset.ExecuteNonQueryAsync(cancellationToken);
                session = sequence = 0; start = -1;
            }
            var items = new List<CaptureTextItem>();
            await using (var query = CaptureCommand(connection, transaction, CaptureItemQuery + """
                 JOIN CaptureSessions s ON s.Id=i.SessionId WHERE s.CaptureGameId=$game
                 AND (i.SessionId,i.ReceiveSequence,i.StartUtf16)>($session,$sequence,$start)
                 ORDER BY i.SessionId,i.ReceiveSequence,i.StartUtf16 LIMIT $limit
                """, ("$game", captureGameId), ("$session", session), ("$sequence", sequence), ("$start", start), ("$limit", batchSize + 1)))
            await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) items.Add(ReadCaptureItem(reader));
            var hasMore = items.Count > batchSize;
            foreach (var item in items.Take(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await IndexAndMatchAsync(connection, transaction, captureGameId, item);
                session = item.SessionId; sequence = item.Sequence; start = item.Start;
            }
            if (dirty || items.Count > 0)
            {
                await using var advance = CaptureCommand(connection, transaction, """
                    UPDATE ScenarioMaterialState SET MatchingRevision=MatchingRevision+1,
                        CursorSession=$session,CursorSequence=$sequence,CursorStart=$start WHERE CaptureGameId=$game
                    """, ("$session", session), ("$sequence", sequence), ("$start", start), ("$game", captureGameId));
                await advance.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new(Math.Min(items.Count, batchSize), hasMore, false);
        }
        finally { _scenarioMatchingGate.Release(); }
    }

    private static async Task IndexAndMatchAsync(SqliteConnection connection, SqliteTransaction transaction, long game, CaptureTextItem item)
    {
        int ordinal;
        await using (var last = CaptureCommand(connection, transaction,
            "SELECT COALESCE(MAX(Ordinal),0)+1 FROM ScenarioSequenceIndex WHERE SessionId=$session", ("$session", item.SessionId)))
            ordinal = Convert.ToInt32(await last.ExecuteScalarAsync());
        var current = new IndexedText(item, ordinal, item.Id, string.IsNullOrWhiteSpace(item.Speaker) ? null : item.Speaker);
        await using (var insert = CaptureCommand(connection, transaction, """
            INSERT INTO ScenarioSequenceIndex(ItemId,CaptureGameId,SessionId,Ordinal,CanonicalItemId,GroupSpeaker)
            VALUES($item,$game,$session,$ordinal,$item,$speaker)
            """, ("$item", item.Id), ("$game", game), ("$session", item.SessionId), ("$ordinal", ordinal), ("$speaker", current.GroupSpeaker)))
            await insert.ExecuteNonQueryAsync();
        MatchProgress progress;
        await using (var find = CaptureCommand(connection, transaction, "SELECT StateJson FROM ScenarioMatchProgress WHERE SessionId=$session", ("$session", item.SessionId)))
            progress = await find.ExecuteScalarAsync() is string json ? JsonSerializer.Deserialize<MatchProgress>(json)! : new();
        var continued = new List<MatchCandidate>();
        foreach (var candidate in progress.Candidates)
        {
            if (candidate.SessionId == item.SessionId && candidate.Start + progress.Length >= progress.TargetStart) continue;
            var next = await ReadIndexedTextAsync(connection, transaction, candidate.SessionId, candidate.Start + progress.Length);
            if (next is not null && SameText(current, next)) continued.Add(candidate);
        }
        if (continued.Count > 0)
        {
            progress.Candidates = continued;
            progress.Length++;
        }
        else progress = new() { SearchFloor = progress.ActiveRangeId is not null ? ordinal : progress.SearchFloor };

        var seed = new List<IndexedText>();
        if (ordinal >= 3)
        {
            seed.Add((await ReadIndexedTextAsync(connection, transaction, item.SessionId, ordinal - 2))!);
            seed.Add((await ReadIndexedTextAsync(connection, transaction, item.SessionId, ordinal - 1))!);
            seed.Add(current);
        }
        if (seed.Count == 3 && seed.All(x => Matchable(x.Item)))
        {
            var hash = SeedHash(seed);
            await using var setHash = CaptureCommand(connection, transaction, "UPDATE ScenarioSequenceIndex SET SeedHash=$hash WHERE ItemId=$item",
                ("$hash", hash), ("$item", item.Id));
            await setHash.ExecuteNonQueryAsync();
            if (progress.Candidates.Count == 0 && ordinal - 2 >= progress.SearchFloor
                && seed.Any(x => new StringInfo(x.Item.Text).LengthInTextElements >= 10))
            {
                var candidates = new List<MatchCandidate>();
                await using (var search = CaptureCommand(connection, transaction, """
                    SELECT SessionId,Ordinal-2 FROM ScenarioSequenceIndex
                    WHERE CaptureGameId=$game AND SeedHash=$hash AND (SessionId<$session OR (SessionId=$session AND Ordinal<$start))
                    ORDER BY SessionId,Ordinal
                    """, ("$game", game), ("$hash", hash), ("$session", item.SessionId), ("$start", ordinal - 2)))
                await using (var reader = await search.ExecuteReaderAsync())
                    while (await reader.ReadAsync()) candidates.Add(new(reader.GetInt64(0), reader.GetInt32(1)));
                foreach (var candidate in candidates)
                {
                    var valid = true;
                    for (var i = 0; i < 3 && valid; i++)
                    {
                        var old = await ReadIndexedTextAsync(connection, transaction, candidate.SessionId, candidate.Start + i);
                        valid = old is not null && SameText(seed[i], old);
                    }
                    if (valid) progress.Candidates.Add(candidate);
                }
                progress.TargetStart = ordinal - 2;
                progress.Length = 3;
            }
        }
        if (progress.Candidates.Count > 0)
        {
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in progress.ActiveRangeId is null ? progress.Candidates : [])
            {
                var keys = new List<long>();
                for (var i = 0; i < progress.Length; i++)
                    keys.Add((await ReadIndexedTextAsync(connection, transaction, candidate.SessionId, candidate.Start + i))!.CanonicalId);
                groups.Add(string.Join(':', keys));
            }
            if (progress.ActiveRangeId is not null || groups.Count == 1)
            {
                var candidate = progress.Candidates[0];
                progress.Candidates = [candidate];
                if (progress.ActiveRangeId is null)
                {
                    // The anchor establishes identity; extend back over short matching context too.
                    while (progress.TargetStart > progress.SearchFloor && candidate.Start > 1)
                    {
                        var previous = await ReadIndexedTextAsync(connection, transaction, item.SessionId, progress.TargetStart - 1);
                        var source = await ReadIndexedTextAsync(connection, transaction, candidate.SessionId, candidate.Start - 1);
                        if (previous is null || source is null || previous.CanonicalId != previous.Item.Id || !SameText(previous, source)
                            || candidate.SessionId == item.SessionId && candidate.Start + progress.Length - 1 >= progress.TargetStart - 1) break;
                        progress.TargetStart--; candidate = candidate with { Start = candidate.Start - 1 }; progress.Length++;
                    }
                    progress.Candidates = [candidate];
                    await using var addRange = CaptureCommand(connection, transaction, """
                        INSERT INTO ScenarioMatchRanges(CaptureGameId,RepresentativeSession,RepresentativeStart,RevisitSession,RevisitStart,ItemCount)
                        VALUES($game,$source,$sourceStart,$target,$targetStart,$count) RETURNING Id
                        """, ("$game", game), ("$source", candidate.SessionId), ("$sourceStart", candidate.Start),
                        ("$target", item.SessionId), ("$targetStart", progress.TargetStart), ("$count", progress.Length));
                    progress.ActiveRangeId = (long)(await addRange.ExecuteScalarAsync())!;
                    for (var i = 0; i < progress.Length; i++)
                        await LinkMatchedItemAsync(connection, transaction, item.SessionId, progress.TargetStart + i, candidate.SessionId, candidate.Start + i);
                }
                else
                {
                    await LinkMatchedItemAsync(connection, transaction, item.SessionId, ordinal, candidate.SessionId, candidate.Start + progress.Length - 1);
                    await using var extend = CaptureCommand(connection, transaction,
                        "UPDATE ScenarioMatchRanges SET ItemCount=$count WHERE Id=$id", ("$count", progress.Length), ("$id", progress.ActiveRangeId));
                    await extend.ExecuteNonQueryAsync();
                }
            }
        }
        await using var save = CaptureCommand(connection, transaction, """
            INSERT INTO ScenarioMatchProgress(SessionId,CaptureGameId,StateJson) VALUES($session,$game,$json)
            ON CONFLICT(SessionId) DO UPDATE SET StateJson=excluded.StateJson
            """, ("$session", item.SessionId), ("$game", game), ("$json", JsonSerializer.Serialize(progress)));
        await save.ExecuteNonQueryAsync();
    }

    private static async Task LinkMatchedItemAsync(SqliteConnection connection, SqliteTransaction transaction,
        long targetSession, int targetOrdinal, long sourceSession, int sourceOrdinal)
    {
        var target = (await ReadIndexedTextAsync(connection, transaction, targetSession, targetOrdinal))!;
        var source = (await ReadIndexedTextAsync(connection, transaction, sourceSession, sourceOrdinal))!;
        await using var update = CaptureCommand(connection, transaction, """
            UPDATE ScenarioSequenceIndex SET CanonicalItemId=$canonical WHERE ItemId=$item;
            UPDATE ScenarioSequenceIndex SET GroupSpeaker=COALESCE(GroupSpeaker,$speaker) WHERE ItemId=$canonical;
            """, ("$canonical", source.CanonicalId), ("$item", target.Item.Id),
            ("$speaker", string.IsNullOrWhiteSpace(target.Item.Speaker) ? null : target.Item.Speaker));
        await update.ExecuteNonQueryAsync();
    }
}
