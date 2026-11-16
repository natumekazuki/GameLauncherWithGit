using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.Data.Sqlite;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    private static async Task EnsureScenarioFlowSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ScenarioFlowCuts (
                SessionId INTEGER NOT NULL REFERENCES CaptureSessions(Id) ON DELETE CASCADE,
                ReceiveSequence INTEGER NOT NULL, OffsetUtf16 INTEGER NOT NULL CHECK(OffsetUtf16 >= 0),
                PRIMARY KEY(SessionId,ReceiveSequence,OffsetUtf16));
            CREATE TABLE IF NOT EXISTS ScenarioFlowRegions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id),
                StartSessionId INTEGER NOT NULL REFERENCES CaptureSessions(Id) ON DELETE CASCADE,
                StartSequence INTEGER NOT NULL, StartOffset INTEGER NOT NULL,
                EndSessionId INTEGER NOT NULL REFERENCES CaptureSessions(Id) ON DELETE CASCADE,
                EndSequence INTEGER NOT NULL, EndOffset INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_ScenarioFlowRegions_Game ON ScenarioFlowRegions(CaptureGameId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertScenarioFlowCutAsync(SqliteConnection connection, SqliteTransaction transaction,
        long profileId, CapturePosition before)
    {
        if (await CaptureOwnerAsync(connection, transaction, before.SessionId) != profileId)
            throw new ArgumentException("このゲームの出現位置を指定してください。");
        await using var insert = CaptureCommand(connection, transaction, """
            INSERT INTO ScenarioFlowCuts(SessionId,ReceiveSequence,OffsetUtf16)
            VALUES($session,$sequence,$offset) ON CONFLICT DO NOTHING
            """, ("$session", before.SessionId), ("$sequence", before.Sequence), ("$offset", before.Offset));
        if (await insert.ExecuteNonQueryAsync() > 0)
            await BumpScenarioMaterialRevisionAsync(connection, transaction, profileId);
    }

    public async Task<ScenarioFlowOverrides> GetScenarioFlowOverridesAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        var cuts = new List<CapturePosition>();
        await using (var query = CaptureCommand(connection, transaction, """
            SELECT c.SessionId,c.ReceiveSequence,c.OffsetUtf16 FROM ScenarioFlowCuts c
            JOIN CaptureSessions s ON s.Id=c.SessionId WHERE s.CaptureGameId=$game
            ORDER BY c.SessionId,c.ReceiveSequence,c.OffsetUtf16
            """, ("$game", profileId)))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) cuts.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2)));
        var regions = new List<ScenarioFlowRegion>();
        await using (var query = CaptureCommand(connection, transaction, """
            SELECT Id,StartSessionId,StartSequence,StartOffset,EndSessionId,EndSequence,EndOffset
            FROM ScenarioFlowRegions WHERE CaptureGameId=$game ORDER BY Id
            """, ("$game", profileId)))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) regions.Add(new(reader.GetInt64(0),
                new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)),
                new(reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6))));
        await transaction.CommitAsync();
        return new(cuts, regions);
    }

    public async Task SetScenarioFlowCutAsync(long profileId, CapturePosition before)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        await ValidateScenarioFlowPositionAsync(connection, transaction, profileId, before, end: false);
        await InsertScenarioFlowCutAsync(connection, transaction, profileId, before);
        await transaction.CommitAsync();
    }

    public async Task<long> CreateIndependentScenarioFlowAsync(long profileId, CapturePosition start, CapturePosition end)
    {
        if (start.CompareTo(end) >= 0) throw new ArgumentException("開始より後の終了位置を指定してください。");
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        await ValidateScenarioFlowPositionAsync(connection, transaction, profileId, start, end: false);
        await ValidateScenarioFlowPositionAsync(connection, transaction, profileId, end, end: true);
        await using (var query = CaptureCommand(connection, transaction, """
            SELECT StartSessionId,StartSequence,StartOffset,EndSessionId,EndSequence,EndOffset
            FROM ScenarioFlowRegions WHERE CaptureGameId=$game
            """, ("$game", profileId)))
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                var existingStart = new CapturePosition(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2));
                var existingEnd = new CapturePosition(reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5));
                if (start.CompareTo(existingEnd) < 0 && existingStart.CompareTo(end) < 0)
                    throw new InvalidOperationException("すでに独立したフローチャートと重なる範囲です。");
            }
        await using var insert = CaptureCommand(connection, transaction, """
            INSERT INTO ScenarioFlowRegions(CaptureGameId,StartSessionId,StartSequence,StartOffset,EndSessionId,EndSequence,EndOffset)
            VALUES($game,$ss,$sq,$so,$es,$eq,$eo); SELECT last_insert_rowid();
            """, ("$game", profileId), ("$ss", start.SessionId), ("$sq", start.Sequence), ("$so", start.Offset),
            ("$es", end.SessionId), ("$eq", end.Sequence), ("$eo", end.Offset));
        var id = (long)(await insert.ExecuteScalarAsync())!;
        await BumpScenarioMaterialRevisionAsync(connection, transaction, profileId);
        await transaction.CommitAsync();
        return id;
    }

    private static async Task ValidateScenarioFlowPositionAsync(SqliteConnection connection, SqliteTransaction transaction,
        long profileId, CapturePosition position, bool end)
    {
        if (await CaptureOwnerAsync(connection, transaction, position.SessionId) != profileId)
            throw new ArgumentException("このゲームの出現位置を指定してください。");
        await using var query = CaptureCommand(connection, transaction, """
            SELECT e.RawText FROM CaptureEvents e WHERE e.SessionId=$session AND e.ReceiveSequence=$sequence
            AND EXISTS(SELECT 1 FROM CaptureItems i WHERE i.SessionId=e.SessionId AND i.ReceiveSequence=e.ReceiveSequence)
            """, ("$session", position.SessionId), ("$sequence", position.Sequence));
        if (await query.ExecuteScalarAsync() is not string raw || position.Offset < 0
            || position.Offset > raw.Length || (!end && position.Offset == raw.Length))
            throw new ArgumentException("保存されている本文の位置を指定してください。");
        if (position.Offset > 0 && position.Offset < raw.Length)
            ScenarioTextProcessor.ValidateBoundaries(raw, [position.Offset]);
    }
}
