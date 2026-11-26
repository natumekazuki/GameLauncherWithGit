using GameLauncherWithGit.Application.Models;
using Microsoft.Data.Sqlite;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    private static async Task EnsureScenarioSceneFlowSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await using (var schema = CaptureCommand(connection, transaction, """
            CREATE TABLE IF NOT EXISTS ScenarioGameTags (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id), Name TEXT NOT NULL,
                UNIQUE(CaptureGameId,Name));
            CREATE TABLE IF NOT EXISTS ScenarioScenes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id), Title TEXT NOT NULL,
                DisplayNumber TEXT NULL, Chapter TEXT NULL, X REAL NOT NULL, Y REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_ScenarioScenes_Game ON ScenarioScenes(CaptureGameId,Id);
            CREATE TABLE IF NOT EXISTS ScenarioSceneRanges (
                SceneId INTEGER NOT NULL REFERENCES ScenarioScenes(Id) ON DELETE CASCADE,
                RangeId INTEGER NOT NULL REFERENCES CaptureRanges(Id) ON DELETE CASCADE,
                PRIMARY KEY(SceneId,RangeId));
            CREATE TABLE IF NOT EXISTS ScenarioSceneTags (
                SceneId INTEGER NOT NULL REFERENCES ScenarioScenes(Id) ON DELETE CASCADE,
                TagId INTEGER NOT NULL REFERENCES ScenarioGameTags(Id) ON DELETE CASCADE,
                PRIMARY KEY(SceneId,TagId));
            CREATE TABLE IF NOT EXISTS ScenarioSceneConnections (
                FromSceneId INTEGER NOT NULL REFERENCES ScenarioScenes(Id) ON DELETE CASCADE,
                ToSceneId INTEGER NOT NULL REFERENCES ScenarioScenes(Id) ON DELETE CASCADE,
                PRIMARY KEY(FromSceneId,ToSceneId));
            """))
            await schema.ExecuteNonQueryAsync(cancellationToken);

        var legacyTags = false;
        await using (var columns = CaptureCommand(connection, transaction, "PRAGMA table_info(CaptureRangeTags)"))
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) legacyTags |= reader.GetString(1) == "Tag";
        if (legacyTags)
        {
            // Preserve names, case distinctions, range identities and per-game ownership in one migration transaction.
            await using var migrate = CaptureCommand(connection, transaction, """
                INSERT OR IGNORE INTO ScenarioGameTags(CaptureGameId,Name)
                    SELECT DISTINCT r.CaptureGameId,t.Tag FROM CaptureRangeTags t JOIN CaptureRanges r ON r.Id=t.RangeId
                    ORDER BY r.CaptureGameId,t.Tag COLLATE BINARY;
                CREATE TABLE CaptureRangeTags_Migrating (
                    RangeId INTEGER NOT NULL REFERENCES CaptureRanges(Id) ON DELETE CASCADE,
                    TagId INTEGER NOT NULL REFERENCES ScenarioGameTags(Id) ON DELETE CASCADE,
                    PRIMARY KEY(RangeId,TagId));
                INSERT INTO CaptureRangeTags_Migrating(RangeId,TagId)
                    SELECT t.RangeId,g.Id FROM CaptureRangeTags t JOIN CaptureRanges r ON r.Id=t.RangeId
                    JOIN ScenarioGameTags g ON g.CaptureGameId=r.CaptureGameId AND g.Name=t.Tag;
                """);
            await migrate.ExecuteNonQueryAsync(cancellationToken);
            await using (var state = CaptureCommand(connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='ScenarioMaterialState')"))
                if ((long)(await state.ExecuteScalarAsync(cancellationToken))! != 0)
                {
                    await using var bump = CaptureCommand(connection, transaction, """
                        UPDATE ScenarioMaterialState SET Revision=Revision+1
                        WHERE CaptureGameId IN (SELECT DISTINCT r.CaptureGameId FROM CaptureRangeTags t JOIN CaptureRanges r ON r.Id=t.RangeId);
                        """);
                    await bump.ExecuteNonQueryAsync(cancellationToken);
                }
            await using var replace = CaptureCommand(connection, transaction, """
                DROP TABLE CaptureRangeTags;
                ALTER TABLE CaptureRangeTags_Migrating RENAME TO CaptureRangeTags;
                """);
            await replace.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var create = CaptureCommand(connection, transaction, """
                CREATE TABLE IF NOT EXISTS CaptureRangeTags (
                    RangeId INTEGER NOT NULL REFERENCES CaptureRanges(Id) ON DELETE CASCADE,
                    TagId INTEGER NOT NULL REFERENCES ScenarioGameTags(Id) ON DELETE CASCADE,
                    PRIMARY KEY(RangeId,TagId));
                """);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ScenarioSceneFlowSnapshot> GetScenarioSceneFlowAsync(long captureGameId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadScenarioSceneFlowAsync(connection, transaction, captureGameId, cancellationToken);
    }

    private static async Task<ScenarioSceneFlowSnapshot> ReadScenarioSceneFlowAsync(SqliteConnection connection,
        SqliteTransaction transaction, long captureGameId, CancellationToken cancellationToken = default)
    {
        var version = await ReadScenarioMaterialVersionAsync(connection, transaction, captureGameId, cancellationToken);
        var profileId = await ProfileForRecordAsync(connection, transaction, captureGameId, cancellationToken);
        var tags = new List<ScenarioGameTag>();
        await using (var query = CaptureCommand(connection, transaction,
            "SELECT Id,Name FROM ScenarioGameTags WHERE CaptureGameId=$game ORDER BY Name COLLATE BINARY,Id", ("$game", profileId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) tags.Add(new(reader.GetInt64(0), reader.GetString(1)));
        var rangeTags = new List<ScenarioRangeTagLink>();
        await using (var query = CaptureCommand(connection, transaction, """
            SELECT t.RangeId,t.TagId FROM CaptureRangeTags t JOIN CaptureRanges r ON r.Id=t.RangeId
            WHERE r.CaptureGameId=$game ORDER BY t.RangeId,t.TagId
            """, ("$game", captureGameId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) rangeTags.Add(new(reader.GetInt64(0), reader.GetInt64(1)));
        var rangeIds = await ReadSceneLinksAsync(connection, transaction, captureGameId, "ScenarioSceneRanges", "RangeId", cancellationToken);
        var tagIds = await ReadSceneLinksAsync(connection, transaction, captureGameId, "ScenarioSceneTags", "TagId", cancellationToken);
        var scenes = new List<ScenarioScene>();
        await using (var query = CaptureCommand(connection, transaction, """
            SELECT Id,Title,DisplayNumber,Chapter,X,Y FROM ScenarioScenes WHERE CaptureGameId=$game ORDER BY Id
            """, ("$game", captureGameId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                scenes.Add(new(id, new(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDouble(4), reader.GetDouble(5),
                    rangeIds.GetValueOrDefault(id) ?? [], tagIds.GetValueOrDefault(id) ?? [])));
            }
        var connections = new List<ScenarioSceneConnection>();
        await using (var query = CaptureCommand(connection, transaction, """
            SELECT e.FromSceneId,e.ToSceneId FROM ScenarioSceneConnections e JOIN ScenarioScenes s ON s.Id=e.FromSceneId
            WHERE s.CaptureGameId=$game ORDER BY e.FromSceneId,e.ToSceneId
            """, ("$game", captureGameId)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) connections.Add(new(reader.GetInt64(0), reader.GetInt64(1)));
        return new(captureGameId, version, tags, rangeTags, scenes, connections);
    }

    private static async Task<Dictionary<long, List<long>>> ReadSceneLinksAsync(SqliteConnection connection,
        SqliteTransaction transaction, long game, string table, string column, CancellationToken cancellationToken)
    {
        var links = new Dictionary<long, List<long>>();
        await using var query = CaptureCommand(connection, transaction, $"""
            SELECT l.SceneId,l.{column} FROM {table} l JOIN ScenarioScenes s ON s.Id=l.SceneId
            WHERE s.CaptureGameId=$game ORDER BY l.SceneId,l.{column}
            """, ("$game", game));
        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            if (!links.TryGetValue(id, out var values)) links[id] = values = [];
            values.Add(reader.GetInt64(1));
        }
        return links;
    }

    private static string NormalizeSceneName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value = value.Trim();
        if (value.Length == 0 || value.IndexOfAny(['\r', '\n']) >= 0) throw new ArgumentException("名前を空でない1行で入力してください。");
        return value;
    }

    private static string? NormalizeOptionalSceneName(string? value) => string.IsNullOrWhiteSpace(value) ? null : NormalizeSceneName(value);

    private static async Task RequireSceneOwnerAsync(SqliteConnection connection, SqliteTransaction transaction,
        long game, string table, long id, CancellationToken cancellationToken)
    {
        await using var query = CaptureCommand(connection, transaction,
            $"SELECT EXISTS(SELECT 1 FROM {table} WHERE Id=$id AND CaptureGameId=$game)", ("$id", id), ("$game", game));
        if ((long)(await query.ExecuteScalarAsync(cancellationToken))! == 0) throw new ArgumentException("このゲームの対象を選んでください。");
    }

    public Task<long> CreateScenarioSceneAsync(long captureGameId, ScenarioSceneEdit edit, CancellationToken cancellationToken = default) =>
        SaveScenarioSceneAsync(captureGameId, null, edit, cancellationToken);

    public async Task UpdateScenarioSceneAsync(long captureGameId, long sceneId, ScenarioSceneEdit edit, CancellationToken cancellationToken = default) =>
        _ = await SaveScenarioSceneAsync(captureGameId, sceneId, edit, cancellationToken);

    public async Task UpdateDisplayedScenarioSceneAsync(long captureGameId, ScenarioScene displayed, ScenarioSceneEdit edit,
        CancellationToken cancellationToken = default) =>
        _ = await SaveScenarioSceneAsync(captureGameId, displayed.Id, edit, cancellationToken, displayed);

    private async Task<long> SaveScenarioSceneAsync(long game, long? sceneId, ScenarioSceneEdit edit,
        CancellationToken cancellationToken, ScenarioScene? displayed = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(edit.RangeIds);
        ArgumentNullException.ThrowIfNull(edit.TagIds);
        if (!double.IsFinite(edit.X) || !double.IsFinite(edit.Y)) throw new ArgumentException("場面の位置には有限の座標を指定してください。");
        edit = edit with { Title = NormalizeSceneName(edit.Title), DisplayNumber = NormalizeOptionalSceneName(edit.DisplayNumber),
            Chapter = NormalizeOptionalSceneName(edit.Chapter), RangeIds = edit.RangeIds.Distinct().Order().ToArray(), TagIds = edit.TagIds.Distinct().Order().ToArray() };
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await ReadScenarioMaterialVersionAsync(connection, transaction, game, cancellationToken);
        if (displayed is not null)
            RequireDisplayedScene(await ReadScenarioSceneFlowAsync(connection, transaction, game, cancellationToken), displayed);
        foreach (var id in edit.RangeIds) await RequireSceneOwnerAsync(connection, transaction, game, "CaptureRanges", id, cancellationToken);
        if (edit.TagIds.Count > 0)
        {
            var profileId = await ProfileForRecordAsync(connection, transaction, game, cancellationToken);
            foreach (var id in edit.TagIds) await RequireSceneOwnerAsync(connection, transaction, profileId, "ScenarioGameTags", id, cancellationToken);
        }
        if (sceneId is not null) await RequireSceneOwnerAsync(connection, transaction, game, "ScenarioScenes", sceneId.Value, cancellationToken);
        var changed = false;
        if (sceneId is null)
        {
            await using var insert = CaptureCommand(connection, transaction, """
                INSERT INTO ScenarioScenes(CaptureGameId,Title,DisplayNumber,Chapter,X,Y) VALUES($game,$title,$number,$chapter,$x,$y) RETURNING Id
                """, ("$game", game), ("$title", edit.Title), ("$number", edit.DisplayNumber), ("$chapter", edit.Chapter), ("$x", edit.X), ("$y", edit.Y));
            sceneId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
            changed = true;
        }
        else
        {
            await using var update = CaptureCommand(connection, transaction, """
                UPDATE ScenarioScenes SET Title=$title,DisplayNumber=$number,Chapter=$chapter,X=$x,Y=$y WHERE Id=$id
                AND (Title IS NOT $title OR DisplayNumber IS NOT $number OR Chapter IS NOT $chapter OR X IS NOT $x OR Y IS NOT $y)
                """, ("$id", sceneId), ("$title", edit.Title), ("$number", edit.DisplayNumber), ("$chapter", edit.Chapter), ("$x", edit.X), ("$y", edit.Y));
            changed = await update.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        changed |= await WriteSceneLinksAsync(connection, transaction, sceneId.Value, "ScenarioSceneRanges", "RangeId", edit.RangeIds, cancellationToken);
        changed |= await WriteSceneLinksAsync(connection, transaction, sceneId.Value, "ScenarioSceneTags", "TagId", edit.TagIds, cancellationToken);
        if (changed) await BumpScenarioMaterialRevisionAsync(connection, transaction, game);
        await transaction.CommitAsync(cancellationToken);
        return sceneId.Value;
    }

    private static async Task<bool> WriteSceneLinksAsync(SqliteConnection connection, SqliteTransaction transaction,
        long scene, string table, string column, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var existing = new List<long>();
        await using (var query = CaptureCommand(connection, transaction, $"SELECT {column} FROM {table} WHERE SceneId=$id ORDER BY {column}", ("$id", scene)))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetInt64(0));
        if (existing.SequenceEqual(ids)) return false;
        await using (var delete = CaptureCommand(connection, transaction, $"DELETE FROM {table} WHERE SceneId=$id", ("$id", scene)))
            await delete.ExecuteNonQueryAsync(cancellationToken);
        foreach (var id in ids)
        {
            await using var insert = CaptureCommand(connection, transaction, $"INSERT INTO {table}(SceneId,{column}) VALUES($scene,$id)", ("$scene", scene), ("$id", id));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        return true;
    }

    public async Task DeleteScenarioSceneAsync(long captureGameId, long sceneId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await RequireSceneOwnerAsync(connection, transaction, captureGameId, "ScenarioScenes", sceneId, cancellationToken);
        await using var delete = CaptureCommand(connection, transaction, "DELETE FROM ScenarioScenes WHERE Id=$id", ("$id", sceneId));
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await BumpScenarioMaterialRevisionAsync(connection, transaction, captureGameId);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task SetScenarioSceneConnectionAsync(long captureGameId, long fromSceneId, long toSceneId, bool connected,
        CancellationToken cancellationToken = default) => WriteScenarioSceneConnectionAsync(captureGameId, fromSceneId, toSceneId,
            connected, cancellationToken);

    public Task SetDisplayedScenarioSceneConnectionAsync(long captureGameId, ScenarioScene from, ScenarioScene to, bool connected,
        CancellationToken cancellationToken = default) => WriteScenarioSceneConnectionAsync(captureGameId, from.Id, to.Id,
            connected, cancellationToken, from, to);

    private async Task WriteScenarioSceneConnectionAsync(long captureGameId, long fromSceneId, long toSceneId, bool connected,
        CancellationToken cancellationToken, ScenarioScene? displayedFrom = null, ScenarioScene? displayedTo = null)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        if (displayedFrom is not null && displayedTo is not null)
        {
            var shown = await ReadScenarioSceneFlowAsync(connection, transaction, captureGameId, cancellationToken);
            RequireDisplayedScene(shown, displayedFrom);
            RequireDisplayedScene(shown, displayedTo);
            if (shown.Connections.Any(x => x.FromSceneId == fromSceneId && x.ToSceneId == toSceneId) == connected)
                throw new InvalidOperationException("接続が更新されました。読み込み直してください。");
        }
        foreach (var scene in new[] { fromSceneId, toSceneId })
            await RequireSceneOwnerAsync(connection, transaction, captureGameId, "ScenarioScenes", scene, cancellationToken);
        await using var command = CaptureCommand(connection, transaction, connected
            ? "INSERT INTO ScenarioSceneConnections(FromSceneId,ToSceneId) VALUES($from,$to) ON CONFLICT DO NOTHING"
            : "DELETE FROM ScenarioSceneConnections WHERE FromSceneId=$from AND ToSceneId=$to", ("$from", fromSceneId), ("$to", toSceneId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) > 0) await BumpScenarioMaterialRevisionAsync(connection, transaction, captureGameId);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void RequireDisplayedScene(ScenarioSceneFlowSnapshot flow, ScenarioScene scene)
    {
        var current = flow.Scenes.SingleOrDefault(x => x.Id == scene.Id)?.Definition;
        var expected = scene.Definition;
        if (current is null || current != (expected with { RangeIds = current.RangeIds, TagIds = current.TagIds })
            || !current.RangeIds.SequenceEqual(expected.RangeIds) || !current.TagIds.SequenceEqual(expected.TagIds))
            throw new InvalidOperationException("場面が更新されました。読み込み直してください。");
    }

    private static async Task<(long Id, bool Created)> GetOrCreateSceneTagAsync(SqliteConnection connection,
        SqliteTransaction transaction, long profileId, string name, CancellationToken cancellationToken = default)
    {
        await using var insert = CaptureCommand(connection, transaction,
            "INSERT INTO ScenarioGameTags(CaptureGameId,Name) VALUES($game,$name) ON CONFLICT DO NOTHING", ("$game", profileId), ("$name", name));
        var created = await insert.ExecuteNonQueryAsync(cancellationToken) > 0;
        await using var query = CaptureCommand(connection, transaction,
            "SELECT Id FROM ScenarioGameTags WHERE CaptureGameId=$game AND Name=$name", ("$game", profileId), ("$name", name));
        return ((long)(await query.ExecuteScalarAsync(cancellationToken))!, created);
    }

    public async Task<long> GetOrCreateScenarioGameTagAsync(long captureGameId, string name, CancellationToken cancellationToken = default)
    {
        name = NormalizeSceneName(name);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await ReadScenarioMaterialVersionAsync(connection, transaction, captureGameId, cancellationToken);
        var profileId = await ProfileForRecordAsync(connection, transaction, captureGameId, cancellationToken);
        var result = await GetOrCreateSceneTagAsync(connection, transaction, profileId, name, cancellationToken);
        if (result.Created) await BumpSharedTagRevisionAsync(connection, transaction, profileId, cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result.Id;
    }

    public async Task RenameScenarioGameTagAsync(long captureGameId, long tagId, string name, CancellationToken cancellationToken = default)
    {
        name = NormalizeSceneName(name);
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var profileId = await ProfileForRecordAsync(connection, transaction, captureGameId, cancellationToken);
        await RequireSceneOwnerAsync(connection, transaction, profileId, "ScenarioGameTags", tagId, cancellationToken);
        await using (var duplicate = CaptureCommand(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM ScenarioGameTags WHERE CaptureGameId=$game AND Name=$name AND Id<>$id)",
            ("$game", profileId), ("$name", name), ("$id", tagId)))
            if ((long)(await duplicate.ExecuteScalarAsync(cancellationToken))! != 0) throw new ArgumentException("同じ名前のタグが登録されています。");
        await using var update = CaptureCommand(connection, transaction,
            "UPDATE ScenarioGameTags SET Name=$name WHERE Id=$id AND Name IS NOT $name", ("$name", name), ("$id", tagId));
        if (await update.ExecuteNonQueryAsync(cancellationToken) > 0)
            await BumpSharedTagRevisionAsync(connection, transaction, profileId, cancellationToken: cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteScenarioGameTagAsync(long captureGameId, long tagId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        var profileId = await ProfileForRecordAsync(connection, transaction, captureGameId, cancellationToken);
        await RequireSceneOwnerAsync(connection, transaction, profileId, "ScenarioGameTags", tagId, cancellationToken);
        await using var otherLinks = CaptureCommand(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM CaptureRangeTags t JOIN CaptureRanges r ON r.Id=t.RangeId
                          WHERE t.TagId=$id AND r.CaptureGameId<>$record)
                OR EXISTS(SELECT 1 FROM ScenarioSceneTags t JOIN ScenarioScenes s ON s.Id=t.SceneId
                          WHERE t.TagId=$id AND s.CaptureGameId<>$record)
            """, ("$id", tagId), ("$record", captureGameId));
        if ((long)(await otherLinks.ExecuteScalarAsync(cancellationToken))! != 0)
            throw new InvalidOperationException("別の記録範囲で使われている作品タグは削除できません。");
        await using var rangeLinks = CaptureCommand(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM CaptureRangeTags WHERE TagId=$id)", ("$id", tagId));
        var revisionUpdatedByRangeLinks = (long)(await rangeLinks.ExecuteScalarAsync(cancellationToken))! != 0;
        await using var delete = CaptureCommand(connection, transaction, "DELETE FROM ScenarioGameTags WHERE Id=$id", ("$id", tagId));
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await BumpSharedTagRevisionAsync(connection, transaction, profileId,
            revisionUpdatedByRangeLinks ? captureGameId : null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Unfiltered material includes even unused tags. A definition change invalidates every scope of this profile,
    // while assignment-only changes stay local. Skip a record already advanced by the range-link triggers.
    private static async Task BumpSharedTagRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        long profileId, long? alreadyUpdatedRecord = null, CancellationToken cancellationToken = default)
    {
        await using var update = CaptureCommand(connection, transaction, """
            UPDATE ScenarioMaterialState SET Revision=Revision+1
            WHERE (CaptureGameId=$profile OR CaptureGameId IN (SELECT RecordId FROM ScenarioBranchHistory WHERE ProfileId=$profile))
                AND CaptureGameId IS NOT $alreadyUpdated
            """, ("$profile", profileId), ("$alreadyUpdated", alreadyUpdatedRecord));
        await update.ExecuteNonQueryAsync(cancellationToken);
    }
}
