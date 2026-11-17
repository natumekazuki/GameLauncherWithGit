using GameLauncherWithGit.Application.Models;
using System.Text.Json;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class SceneFlowStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(), "scene-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteGameLibraryStore _store;
    private string Database => Path.Combine(_directory, "library.db");
    private static readonly CaptureSource Source = new(10, 100, 200, 0, "H", "body");
    private static readonly string[] Lines = ["「共通の長い冒頭の本文です」", "「共通の長い二番目の本文です」", "「共通の長い三番目の本文です」"];
    public SceneFlowStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _store = OpenStore();
    }
    private SqliteGameLibraryStore OpenStore() => new(NullLogger<SqliteGameLibraryStore>.Instance, Database);
    private async Task<(long Game, long Session, long Range)> FixtureAsync(string id = "game")
    {
        await _store.UpsertAsync(new(id, id, "synthetic.exe", null, null, null, GameCardStatus.Unknown));
        var game = (await _store.GetOrCreateCaptureProfileAsync(id)).Id;
        var session = await _store.BeginCaptureSessionAsync(game);
        for (var index = 0; index < Lines.Length; index++)
            await _store.AppendCaptureAsync(session, new(index + 1, DateTimeOffset.UtcNow, Source, Lines[index], CaptureRole.Body), new());
        var range = await _store.SaveCaptureRangeAsync(game, "本文区間", new(session, 1, 0), new(session, 3, Lines[2].Length), ["共通"]);
        return (game, session, range);
    }
    private static ScenarioSceneEdit Edit(string title, params long[] ranges) => new(title, "1", "第一章", 10, 20, ranges, []);
    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, ForeignKeys = true }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "手動場面の編集/再起動/追加本文照合でIDと手動構造を保ち、場面削除は元本文/区間/画像を消さない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動場面フロー" }
    // fault = "表示情報でIDを生成する、保存後に編集を失う、または場面削除を元記録へcascadeする"
    // observable = "公開scene snapshotのID/定義/接続、再起動と追加本文照合後の読出し、元本文/区間/画像metadata"
    // observation_boundary = "public-boundary"
    // scope = "synthetic scene CRUD, directed branch/join and persistence"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task SceneEditsAndConnectionsPersistWithoutChangingRawRecords()
    {
        var (game, session, range) = await FixtureAsync();
        var raw = await _store.GetCaptureItemsAsync(session, 0);
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), new(1, 1, new string('a', 64), [1]), DateTimeOffset.UtcNow);
        var first = await _store.CreateScenarioSceneAsync(game, Edit("導入", range));
        var left = await _store.CreateScenarioSceneAsync(game, Edit("左分岐", range));
        var right = await _store.CreateScenarioSceneAsync(game, Edit("右分岐"));
        var join = await _store.CreateScenarioSceneAsync(game, Edit("再合流", range));
        foreach (var (from, to) in new[] { (first, left), (first, right), (left, join), (right, join) })
            await _store.SetScenarioSceneConnectionAsync(game, from, to, true);
        var changed = Edit("変更後", range) with { DisplayNumber = "1-2", Chapter = "第二章", X = -40, Y = 72 };
        await _store.UpdateScenarioSceneAsync(game, first, changed);
        var saved = await _store.GetScenarioSceneFlowAsync(game);
        var definition = Assert.Single(saved.Scenes, x => x.Id == first).Definition;
        Assert.Equal(changed.Title, definition.Title); Assert.Equal(changed.DisplayNumber, definition.DisplayNumber);
        Assert.Equal(changed.Chapter, definition.Chapter); Assert.Equal(changed.X, definition.X); Assert.Equal(changed.Y, definition.Y);
        Assert.Equal(changed.RangeIds, definition.RangeIds); Assert.Equal(changed.TagIds, definition.TagIds);
        Assert.Equal(4, saved.Connections.Count);
        Assert.Empty(Assert.Single(saved.Scenes, x => x.Id == right).Definition.RangeIds);
        await _store.SetScenarioSceneConnectionAsync(game, first, left, true);
        await _store.UpdateScenarioSceneAsync(game, first, changed);
        Assert.Equal(saved.Version, (await _store.GetScenarioSceneFlowAsync(game)).Version);
        using var reopened = OpenStore();
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(await reopened.GetScenarioSceneFlowAsync(game)));
        await reopened.AppendCaptureAsync(session, new(4, DateTimeOffset.UtcNow, Source, "「後から追加した長い本文です」", CaptureRole.Body), new());
        await reopened.GetScenarioMaterialAsync(game, new());
        var afterAutomaticWork = await reopened.GetScenarioSceneFlowAsync(game);
        Assert.Equal(JsonSerializer.Serialize(saved.Scenes), JsonSerializer.Serialize(afterAutomaticWork.Scenes));
        Assert.Equal(saved.Connections, afterAutomaticWork.Connections);
        await reopened.DeleteScenarioSceneAsync(game, first);
        var deleted = await reopened.GetScenarioSceneFlowAsync(game);
        Assert.Equal(new[] { left, right, join }, deleted.Scenes.Select(x => x.Id));
        Assert.Equal(2, deleted.Connections.Count);
        Assert.DoesNotContain(deleted.Connections, x => x.FromSceneId == first || x.ToSceneId == first);
        Assert.Equal(raw, (await reopened.GetCaptureItemsAsync(session, 0)).Take(raw.Count));
        Assert.Equal(4, (await reopened.GetCaptureItemsAsync(session, 0)).Count);
        Assert.Equal(range, Assert.Single(await reopened.GetCaptureRangesAsync(game)).Id);
        Assert.Equal(image.ImageId, Assert.Single(Assert.Single(await reopened.GetEventScreenshotMetadataAsync(game, [new(session, 1)])).Images).ImageId);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "共通タグ改名/削除は区間と場面へ反映し、削除の版更新は区間link triggerと重複せず未使用/場面のみは1回、照合状態と正規化同値の資料版を保つ"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動場面フロー" }
    // fault = "区間と場面で名前を二重保存する、話者を自動全転記する、削除で版を二重加算するか更新せず、または編集metadataで本文照合を再構築する"
    // observable = "タグID/区間名/scene TagIds/選択本文、2区間/未使用/場面のみタグ削除直前直後のRevision差分とMatching世代/完了"
    // observation_boundary = "public-boundary"
    // scope = "shared per-game tag master and material-only revisions"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task SharedTagsFollowRenameAndDeletionWithoutRebuildingMatching()
    {
        var (game, session, range) = await FixtureAsync();
        var other = await FixtureAsync("other");
        await _store.SetCaptureSpeakerAsync(session, (await _store.GetCaptureItemsAsync(session, 0))[0].Id, "未選択の話者");
        var tags = (await _store.GetScenarioSceneFlowAsync(game)).Tags;
        var common = Assert.Single(tags);
        Assert.Equal("共通", common.Name);
        Assert.Equal(common.Id, await _store.GetOrCreateScenarioGameTagAsync(game, " 共通 "));
        Assert.DoesNotContain(tags, x => x.Name == "未選択の話者");
        var scene = await _store.CreateScenarioSceneAsync(game, Edit("場面", range) with { TagIds = [common.Id] });
        var noOpVersion = (await _store.GetScenarioSceneFlowAsync(game)).Version;
        Assert.Equal(common.Id, await _store.GetOrCreateScenarioGameTagAsync(game, " 共通 "));
        await _store.RenameScenarioGameTagAsync(game, common.Id, " 共通 ");
        await _store.UpdateCaptureRangeAsync(game, range, " 本文区間 ", [" 共通 ", "共通"]);
        await _store.UpdateScenarioSceneAsync(game, scene, Edit(" 場面 ", range, range) with { TagIds = [common.Id, common.Id] });
        await _store.SetScenarioSceneConnectionAsync(game, scene, scene, false);
        Assert.Equal(noOpVersion, (await _store.GetScenarioSceneFlowAsync(game)).Version);
        var secondRange = await _store.SaveCaptureRangeAsync(game, "同じタグ", new(session, 1, 0), new(session, 1, Lines[0].Length), ["共通"]);
        var baseline = await _store.GetScenarioMaterialAsync(game, new());
        Assert.True(baseline.MatchingComplete);
        await _store.RenameScenarioGameTagAsync(game, common.Id, "共通の改名");
        var renamed = await _store.GetScenarioSceneFlowAsync(game);
        Assert.Equal(common.Id, Assert.Single(renamed.Tags).Id);
        Assert.Equal("共通の改名", Assert.Single(renamed.Tags).Name);
        Assert.All(await _store.GetCaptureRangesAsync(game), x => Assert.Equal(new[] { "共通の改名" }, x.Tags));
        Assert.Equal(new[] { common.Id }, Assert.Single(renamed.Scenes).Definition.TagIds);
        Assert.Equal(new[] { range, secondRange }, renamed.RangeTags.Select(x => x.RangeId));
        Assert.Empty(await _store.GetCaptureExportItemsAsync(game, new(IncludeTags: ["共通"])));
        Assert.Equal(3, (await _store.GetCaptureExportItemsAsync(game, new(IncludeTags: ["共通の改名"]))).Count);
        var unused = await _store.GetOrCreateScenarioGameTagAsync(game, "未使用");
        var selectedSpeaker = await _store.GetOrCreateScenarioGameTagAsync(game, "未選択の話者");
        Assert.Equal(3, (await _store.GetScenarioSceneFlowAsync(game)).Tags.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.RenameScenarioGameTagAsync(game, unused, "共通の改名"));
        var beforeDelete = await _store.GetScenarioMaterialAsync(game, new());
        Assert.Equal(baseline.Version.MatchingRevision, beforeDelete.Version.MatchingRevision);
        await _store.DeleteScenarioGameTagAsync(game, common.Id);
        var after = await _store.GetScenarioMaterialAsync(game, new());
        Assert.Equal(beforeDelete.Version.Revision + 2, after.Version.Revision);
        Assert.True(after.Version.Revision > baseline.Version.Revision);
        Assert.Equal(beforeDelete.Version.MatchingRevision, after.Version.MatchingRevision);
        Assert.Equal(beforeDelete.MatchingComplete, after.MatchingComplete);
        Assert.True(after.MatchingComplete);
        Assert.Equal(3, after.Occurrences.Count);
        var final = await _store.GetScenarioSceneFlowAsync(game);
        Assert.Equal(new[] { unused, selectedSpeaker }.Order(), final.Tags.Select(x => x.Id).Order());
        Assert.Empty(final.RangeTags); Assert.Empty(Assert.Single(final.Scenes, x => x.Id == scene).Definition.TagIds);
        Assert.All(await _store.GetCaptureRangesAsync(game), x => Assert.Empty(x.Tags));
        Assert.Equal("共通", Assert.Single((await _store.GetScenarioSceneFlowAsync(other.Game)).Tags).Name);
        var beforeUnusedDelete = await _store.GetScenarioMaterialAsync(game, new());
        await _store.DeleteScenarioGameTagAsync(game, unused);
        var afterUnusedDelete = await _store.GetScenarioMaterialAsync(game, new());
        Assert.Equal(beforeUnusedDelete.Version.Revision + 1, afterUnusedDelete.Version.Revision);
        Assert.Equal(beforeUnusedDelete.Version.MatchingRevision, afterUnusedDelete.Version.MatchingRevision);
        Assert.True(afterUnusedDelete.MatchingComplete);
        await _store.UpdateScenarioSceneAsync(game, scene, Edit("場面", range) with { TagIds = [selectedSpeaker] });
        var beforeSceneOnlyDelete = await _store.GetScenarioMaterialAsync(game, new());
        await _store.DeleteScenarioGameTagAsync(game, selectedSpeaker);
        var afterSceneOnlyDelete = await _store.GetScenarioMaterialAsync(game, new());
        Assert.Equal(beforeSceneOnlyDelete.Version.Revision + 1, afterSceneOnlyDelete.Version.Revision);
        Assert.Equal(beforeSceneOnlyDelete.Version.MatchingRevision, afterSceneOnlyDelete.Version.MatchingRevision);
        Assert.True(afterSceneOnlyDelete.MatchingComplete);
        var deletedTags = await _store.GetScenarioSceneFlowAsync(game);
        Assert.Empty(deletedTags.Tags); Assert.Empty(Assert.Single(deletedTags.Scenes).Definition.TagIds);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "場面参照は同作品だけを原子的に保存し、区間/本文/全履歴の削除後も手動場面/接続/作品タグを保持する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動場面フロー" }
    // fault = "他作品のrange/tag/edgeを受理する、失敗updateを部分保存する、または履歴clearで手動構造を消す"
    // observable = "拒否後と削除前後の公開scene/range/raw snapshot、資料版、再起動後の空場面"
    // observation_boundary = "public-boundary"
    // scope = "scene reference integrity and deletion ownership"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ReferenceValidationAndHistoryDeletionPreserveAuthoredStructure()
    {
        var (game, session, range) = await FixtureAsync();
        var other = await FixtureAsync("other");
        var tag = Assert.Single((await _store.GetScenarioSceneFlowAsync(game)).Tags).Id;
        var foreignTag = Assert.Single((await _store.GetScenarioSceneFlowAsync(other.Game)).Tags).Id;
        var first = await _store.CreateScenarioSceneAsync(game, Edit("場面", range) with { TagIds = [tag] });
        var empty = await _store.CreateScenarioSceneAsync(game, Edit("独立"));
        var foreignScene = await _store.CreateScenarioSceneAsync(other.Game, Edit("別作品", other.Range));
        await _store.SetScenarioSceneConnectionAsync(game, first, empty, true);
        var before = await _store.GetScenarioSceneFlowAsync(game);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpdateScenarioSceneAsync(game, first, Edit("不成立", other.Range)));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpdateScenarioSceneAsync(game, first, Edit("不成立", range) with { TagIds = [foreignTag] }));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetScenarioSceneConnectionAsync(game, first, foreignScene, true));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteScenarioSceneAsync(game, foreignScene));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteScenarioGameTagAsync(game, foreignTag));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateScenarioSceneAsync(game, Edit("不成立") with { X = double.NaN }));
        Assert.Equal(before.Version, (await _store.GetScenarioSceneFlowAsync(game)).Version);
        Assert.Equal("場面", (await _store.GetScenarioSceneFlowAsync(game)).Scenes[0].Definition.Title);
        var failureTag = await _store.GetOrCreateScenarioGameTagAsync(game, "故障注入");
        var beforeFailure = await _store.GetScenarioSceneFlowAsync(game);
        await ExecuteAsync($"CREATE TRIGGER fail_scene_tag BEFORE INSERT ON ScenarioSceneTags WHEN NEW.TagId={failureTag} BEGIN SELECT RAISE(ABORT,'synthetic scene failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => _store.UpdateScenarioSceneAsync(game, first,
            Edit("保存失敗", range) with { TagIds = [failureTag] }));
        Assert.Equal(JsonSerializer.Serialize(beforeFailure), JsonSerializer.Serialize(await _store.GetScenarioSceneFlowAsync(game)));
        await ExecuteAsync("DROP TRIGGER fail_scene_tag");
        await _store.UpdateCaptureRangeAsync(game, range, "改名区間", ["別タグ"]);
        Assert.Equal(new[] { range }, (await _store.GetScenarioSceneFlowAsync(game)).Scenes[0].Definition.RangeIds);
        await _store.DeleteCaptureEventsAsync(game, [new(session, 1)]);
        Assert.Equal(new[] { range }, (await _store.GetScenarioSceneFlowAsync(game)).Scenes[0].Definition.RangeIds);
        await _store.DeleteCaptureRangeAsync(game, range);
        Assert.Empty((await _store.GetScenarioSceneFlowAsync(game)).Scenes[0].Definition.RangeIds);
        var remainingRange = await _store.SaveCaptureRangeAsync(game, "残存", new(session, 2, 0), new(session, 3, Lines[2].Length));
        await _store.UpdateScenarioSceneAsync(game, first, Edit("場面", remainingRange) with { TagIds = [tag] });
        await _store.ClearCaptureHistoryAsync(game);
        using var reopened = OpenStore();
        var cleared = await reopened.GetScenarioSceneFlowAsync(game);
        Assert.Equal(new[] { first, empty }, cleared.Scenes.Select(x => x.Id));
        Assert.All(cleared.Scenes, x => Assert.Empty(x.Definition.RangeIds));
        Assert.Equal(new ScenarioSceneConnection(first, empty), Assert.Single(cleared.Connections));
        Assert.Contains(cleared.Tags, x => x.Id == tag); Assert.Empty(cleared.RangeTags);
        Assert.Equal(new CaptureHistoryCounts(0, 0, 0), await reopened.GetCaptureHistoryCountsAsync(game));
        Assert.Equal(3, (await reopened.GetCaptureItemsAsync(other.Session, 0)).Count);
    }

    private async Task CreateLegacyTagSchemaAsync(bool failMigration)
    {
        await ExecuteAsync("""
            DROP TABLE ScenarioSceneConnections; DROP TABLE ScenarioSceneTags; DROP TABLE ScenarioSceneRanges;
            DROP TABLE ScenarioScenes; DROP TABLE CaptureRangeTags; DROP TABLE ScenarioGameTags;
            CREATE TABLE CaptureRangeTags(RangeId INTEGER NOT NULL REFERENCES CaptureRanges(Id) ON DELETE CASCADE,
                Tag TEXT NOT NULL, PRIMARY KEY(RangeId,Tag));
            INSERT INTO CaptureRangeTags SELECT Id,'共通' FROM CaptureRanges;
            INSERT INTO CaptureRangeTags SELECT Id,'Tag' FROM CaptureRanges;
            INSERT INTO CaptureRangeTags SELECT Id,'tag' FROM CaptureRanges;
            """);
        if (failMigration) await ExecuteAsync("""
            CREATE TABLE ScenarioGameTags(Id INTEGER PRIMARY KEY AUTOINCREMENT,CaptureGameId INTEGER NOT NULL REFERENCES CaptureGames(Id),
                Name TEXT NOT NULL,UNIQUE(CaptureGameId,Name));
            CREATE TRIGGER fail_migration BEFORE INSERT ON ScenarioGameTags BEGIN SELECT RAISE(ABORT,'synthetic migration failure'); END;
            """);
    }

    // @test-value v2
    // kind = "compatibility"
    // claim = "旧文字列タグの移行は作品別同名/大小文字を保って共通masterへ一度だけ移し元本文/区間/画像と照合世代を維持する"
    // oracle = { type = "contract", ref = "README.md: 互換性とデータ保護; docs/design/scenario-extraction.md: 手動場面フロー" }
    // fault = "作品を跨いでタグを統合する、caseを失う、原記録を再作成する、または毎起動移行を繰り返す"
    // observable = "旧schemaからの公開raw/range/image/tag読出し、資料版、再起動時の同ID/版とforeign_key_check"
    // observation_boundary = "public-boundary"
    // scope = "synthetic deployed legacy range-tag schema upgrade"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task LegacyTagMigrationPreservesExistingDataAndIsIdempotent()
    {
        var (game, session, range) = await FixtureAsync();
        var other = await FixtureAsync("other");
        var raw = await _store.GetCaptureItemsAsync(session, 0);
        var material = await _store.GetScenarioMaterialAsync(game, new());
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), new(1, 1, new string('b', 64), [2]), DateTimeOffset.UtcNow);
        var version = await _store.GetScenarioMaterialVersionAsync(game);
        _store.Dispose();
        await CreateLegacyTagSchemaAsync(false);
        using var upgraded = OpenStore();
        var snapshot = await upgraded.GetScenarioSceneFlowAsync(game);
        var otherSnapshot = await upgraded.GetScenarioSceneFlowAsync(other.Game);
        Assert.Equal(new[] { "Tag", "tag", "共通" }, snapshot.Tags.Select(x => x.Name));
        Assert.Empty(snapshot.Tags.Select(x => x.Id).Intersect(otherSnapshot.Tags.Select(x => x.Id)));
        Assert.All(snapshot.RangeTags, x => Assert.Equal(range, x.RangeId));
        Assert.Equal(3, snapshot.RangeTags.Count);
        Assert.Equal(new[] { "Tag", "tag", "共通" }, Assert.Single(await upgraded.GetCaptureRangesAsync(game)).Tags);
        Assert.Equal(raw, await upgraded.GetCaptureItemsAsync(session, 0));
        Assert.Equal(image.ImageId, Assert.Single(Assert.Single(await upgraded.GetEventScreenshotMetadataAsync(game, [new(session, 1)])).Images).ImageId);
        Assert.Equal(version.Revision + 1, snapshot.Version.Revision);
        Assert.Equal(material.Version.MatchingRevision, snapshot.Version.MatchingRevision);
        using var reopened = OpenStore();
        var repeated = await reopened.GetScenarioSceneFlowAsync(game);
        Assert.Equal(snapshot.Version, repeated.Version); Assert.Equal(snapshot.Tags, repeated.Tags);
        await using var connection = new SqliteConnection($"Data Source={Database}");
        await connection.OpenAsync();
        await using var check = connection.CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await check.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
    }

    // @test-value v2
    // kind = "compatibility"
    // claim = "共通タグmigrationが失敗しても旧タグ/元記録を原子的に保持し、失敗原因解消後に同じDBを正常移行できる"
    // oracle = { type = "contract", ref = "README.md: 互換性とデータ保護; docs/design/scenario-extraction.md: 手動場面フロー" }
    // fault = "旧tableを移行commit前に消すか失敗を無視して部分schema/資料版を保存する"
    // observable = "合成INSERT失敗後の旧Tag列/値と元本文・版、再試行後の公開タグ読出し"
    // observation_boundary = "public-boundary"
    // scope = "SQLite migration failure and recovery without data reset"
    // lifecycle = "permanent"
    // impact = "更新失敗で既存タグや記録が失われる"
    // distinction = "成功時移行testではtransaction途中失敗時の旧schema保持を検証できない"
    // @end-test-value
    [Fact]
    public async Task FailedLegacyMigrationRollsBackAndCanRetryTheSameDatabase()
    {
        var (game, session, _) = await FixtureAsync();
        var raw = await _store.GetCaptureItemsAsync(session, 0);
        var version = await _store.GetScenarioMaterialVersionAsync(game);
        _store.Dispose();
        await CreateLegacyTagSchemaAsync(true);
        using var upgraded = OpenStore();
        await Assert.ThrowsAsync<SqliteException>(() => upgraded.GetScenarioSceneFlowAsync(game));
        await using (var connection = new SqliteConnection($"Data Source={Database}"))
        {
            await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT Tag FROM CaptureRangeTags ORDER BY Tag";
            var tags = new List<string>();
            await using (var reader = await query.ExecuteReaderAsync()) while (await reader.ReadAsync()) tags.Add(reader.GetString(0));
            Assert.Equal(new[] { "Tag", "tag", "共通" }, tags);
            query.CommandText = "SELECT Revision FROM ScenarioMaterialState WHERE CaptureGameId=" + game;
            Assert.Equal(version.Revision, await query.ExecuteScalarAsync());
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('ScenarioScenes','CaptureRangeTags_Migrating')";
            Assert.Equal(0L, await query.ExecuteScalarAsync());
        }
        await ExecuteAsync("DROP TRIGGER fail_migration");
        Assert.Equal(3, (await upgraded.GetScenarioSceneFlowAsync(game)).Tags.Count);
        Assert.Equal(raw, await upgraded.GetCaptureItemsAsync(session, 0));
    }

    public void Dispose()
    {
        _store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true);
    }
}
