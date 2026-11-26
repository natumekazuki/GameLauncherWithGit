using System.IO.Compression;
using System.Text.Json;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioHistoryStoreTests : IDisposable
{
    private const string Repo = "synthetic/repo";
    private const string Main = "refs/heads/main";
    private const string Alternate = "refs/heads/route/alternate";
    private static readonly CaptureSource Source = new(1, 2, 3, 0, "hook", "body");
    private static readonly DateTimeOffset Time = new(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=");
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(),
        "branch-history-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "synthetic.db");

    // @test-value v2
    // kind = "compatibility"
    // claim = "枝対応前の親記録と保存設定をそのまま読み、枝の記録IDを作品・repo・完全refの組で永続化する"
    // oracle = { type = "contract", ref = "IScenarioHistoryStore; Session Graph play-recording-web: scenario-branch-record-vs-profile; README.md#互換性とデータ保護" }
    // fault = "旧履歴をいずれかの枝へ移す、設定を子で上書きする、ref末尾だけで突き合わせる、または記録rootを作品一覧に出す"
    // observable = "旧スコープの資料版・本文・区間、再読込後の作品設定・作品一覧・記録IDと枝本文、別repo/ref/作品のID"
    // observation_boundary = "public-boundary"
    // scope = "SQLite upgrade and persistent scenario history identity"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task UpgradeKeepsLegacyAndReenteringExactIdentityReusesRecord()
    {
        CaptureProfile profile;
        CaptureTextItem legacy;
        long legacyRange;
        ScenarioMaterialVersion legacyVersion;
        using (var before = OpenStore())
        {
            profile = await AddProfileAsync(before, "first");
            await before.SaveCaptureProfileAsync(profile with { BodyExecutablePath = "configured.exe", Rules = new() { Names = ["登録済み候補"] } });
            legacy = await RecordAsync(before, profile.Id, "「従来記録」");
            legacyRange = await SaveRangeAsync(before, profile.Id, legacy, "従来タグ");
            legacyVersion = (await before.GetScenarioMaterialAsync(profile.Id, new())).Version;
        }
        // Recreate the preceding schema boundary in this synthetic database, leaving legacy rows and IDs untouched.
        await ExecuteAsync("""
            DROP TRIGGER ScenarioMaterial_SharedTitleUpdate;
            DROP TRIGGER ScenarioMaterial_SharedLabelUpdate;
            DROP TABLE ScenarioBranchHistory;
            CREATE TRIGGER ScenarioMaterial_LabelUpdate AFTER UPDATE ON CaptureLabels
            WHEN OLD.Name IS NOT NEW.Name OR OLD.Kind IS NOT NEW.Kind BEGIN
                UPDATE ScenarioMaterialState SET Revision=Revision+1,Dirty=1 WHERE CaptureGameId=NEW.CaptureGameId;
            END;
            """);

        using var store = OpenStore();
        IScenarioHistoryStore history = store;
        Assert.Equal(new ScenarioHistoryScope(profile.Id, profile.Id, null, null), Assert.Single(await history.GetScenarioHistoryScopesAsync(profile.Id)));
        var main = await history.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, Main);
        var nested = await history.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, "refs/heads/route/main");
        var caseVariant = await history.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, "refs/heads/Main");
        var otherRepo = await history.GetOrCreateBranchRecordIdAsync(profile.Id, "synthetic/other", Main);
        var other = await AddProfileAsync(store, "second");
        var otherRecord = await history.GetOrCreateBranchRecordIdAsync(other.Id, Repo, Main);
        Assert.Equal(6, new[] { profile.Id, main, nested, caseVariant, otherRepo, otherRecord }.Distinct().Count());
        Assert.Equal(new[] { profile.Id, other.Id }, (await store.GetCaptureProfilesAsync()).Select(x => x.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => history.GetOrCreateBranchRecordIdAsync(main, Repo, Main));
        await Assert.ThrowsAsync<ArgumentException>(() => history.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, "main"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveCaptureProfileAsync(profile with { Id = main }));
        Assert.Empty(await store.GetGameCaptureItemsAsync(main));
        Assert.Equal(legacyVersion, await store.GetScenarioMaterialVersionAsync(profile.Id));
        var branchItem = await RecordAsync(store, main, "「再入する枝の記録」");

        using var reopened = OpenStore();
        var saved = (await reopened.FindCaptureProfileAsync("first"))!;
        Assert.Equal("configured.exe", saved.BodyExecutablePath);
        Assert.Equal(new[] { "登録済み候補" }, saved.Rules.Names);
        Assert.Equal(legacy, Assert.Single(await reopened.GetGameCaptureItemsAsync(profile.Id)));
        var range = Assert.Single(await reopened.GetCaptureRangesAsync(profile.Id));
        Assert.Equal(legacyRange, range.Id);
        Assert.Equal(new[] { "従来タグ" }, range.Tags);
        var reentered = await reopened.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, Main);
        Assert.Equal(main, reentered);
        Assert.Equal(branchItem, Assert.Single(await reopened.GetGameCaptureItemsAsync(reentered)));
        Assert.Empty(await reopened.GetGameCaptureItemsAsync(nested));
        var scopes = await reopened.GetScenarioHistoryScopesAsync(profile.Id);
        Assert.Equal(5, scopes.Count);
        Assert.Contains(new ScenarioHistoryScope(main, profile.Id, Repo, Main), scopes);
        Assert.Equal(otherRecord, Assert.Single(await reopened.GetScenarioHistoryScopesAsync(other.Id), x => x.BranchRef is not null).RecordId);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "一つの枝の保存・読取・全履歴消去は、その枝の本文、画像関連、区間と経路だけを対象にする"
    // oracle = { type = "contract", ref = "Session Graph play-recording-web: scenario-branch-record-vs-profile and scenario-branch-shared-tag-removal; docs/design/scenario-extraction.md: 手動場面フロー" }
    // fault = "親IDを記録保存に使い続ける、または共有repo・画像や話者を理由に別rootの本文・関連・版を漏らすか変更する"
    // observable = "記録root別スナップショット・画像ID、不可な画像/区間/出現指定の例外、消去後の別rootの資料版・話者・画像"
    // observation_boundary = "public-boundary"
    // scope = "SQLite recorded content, image links and flow ownership"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task RecordChangesAndClearDoNotCrossRoots()
    {
        using var store = OpenStore();
        var profile = await AddProfileAsync(store, "first");
        var main = await store.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, Main);
        var alternate = await store.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, Alternate);
        var old = await RecordAsync(store, profile.Id, "「従来記録」", "従来の話者");
        var quiet = await RecordAsync(store, alternate, "「別枝の記録」", "別枝の話者");
        var quietKey = new CaptureEventKey(quiet.SessionId, quiet.Sequence);
        var quietImage = await SaveImageAsync(store, alternate, quietKey);
        var before = (await store.GetScenarioMaterialAsync(alternate, new())).Version;
        var legacy = (await store.GetScenarioMaterialAsync(profile.Id, new())).Version;

        var item = await RecordAsync(store, main, "「この枝の記録」", "この枝の話者");
        var range = await SaveRangeAsync(store, main, item);
        var scene = await store.CreateScenarioSceneAsync(main, Edit("場面", [range]));
        var region = await store.CreateIndependentScenarioFlowAsync(main, item.StartPosition, item.EndPosition);
        await store.SetScenarioFlowCutAsync(main, item.StartPosition);
        var key = new CaptureEventKey(item.SessionId, item.Sequence);
        var image = await SaveImageAsync(store, main, key);
        Assert.Equal(quietImage.ImageId, image.ImageId);
        var material = await store.GetScenarioMaterialAsync(main, new());
        Assert.Equal(main, material.Version.CaptureGameId);
        var occurrence = Assert.Single(material.Occurrences);
        Assert.Equal(item.SessionId, occurrence.Source.SessionId);
        Assert.Equal(image.ImageId, Assert.Single(occurrence.Images).ImageId);
        Assert.Equal(scene, Assert.Single(material.SceneFlow.Scenes).Id);
        Assert.Equal(region, Assert.Single((await store.GetScenarioFlowOverridesAsync(main)).Regions).Id);
        Assert.Equal(item.StartPosition, Assert.Single((await store.GetScenarioFlowOverridesAsync(main)).Cuts));
        Assert.Equal(new[] { "この枝の話者" }, await store.GetGameCaptureSpeakersAsync(main));
        Assert.Equal(new[] { "別枝の話者" }, await store.GetGameCaptureSpeakersAsync(alternate));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetEventScreenshotsAsync(alternate, key));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateScenarioSceneAsync(alternate, Edit("不正な割当", [range])));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetScenarioFlowCutAsync(alternate, item.StartPosition));
        Assert.Equal(before, await store.GetScenarioMaterialVersionAsync(alternate));
        Assert.Equal(legacy, await store.GetScenarioMaterialVersionAsync(profile.Id));

        await store.ClearCaptureHistoryAsync(main);
        Assert.Empty(await store.GetGameCaptureItemsAsync(main));
        Assert.Empty(await store.GetCaptureRangesAsync(main));
        var empty = await store.GetScenarioFlowOverridesAsync(main);
        Assert.Empty(empty.Regions);
        Assert.Empty(empty.Cuts);
        var retainedScene = Assert.Single((await store.GetScenarioSceneFlowAsync(main)).Scenes);
        Assert.Equal(scene, retainedScene.Id);
        Assert.Empty(retainedScene.Definition.RangeIds);
        Assert.Equal(old, Assert.Single(await store.GetGameCaptureItemsAsync(profile.Id)));
        Assert.Equal(quiet, Assert.Single(await store.GetGameCaptureItemsAsync(alternate)));
        Assert.Equal(Png, await store.ReadEventScreenshotPngAsync(alternate, quietKey, image.ImageId));
        Assert.Equal(before, await store.GetScenarioMaterialVersionAsync(alternate));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "枝間でタグ定義を共有しても割当は混ざらず、他rootで使用中の定義は削除できない"
    // oracle = { type = "contract", ref = "IScenarioSceneFlowStore.DeleteScenarioGameTagAsync and RenameScenarioGameTagAsync; Session Graph play-recording-web: scenario-branch-shared-tag-removal" }
    // fault = "定義を枝ごとに作る、改名を割当の再作成にする、別rootの区間または場面の割当を削除する、または共有定義の変更通知を別作品に伝える"
    // observable = "共有タグID・名前・各rootの割当、削除拒否時の資料版、割当解除後の削除結果・照合世代・別作品の資料版"
    // observation_boundary = "public-boundary"
    // scope = "SQLite shared tag definitions and root-local assignments"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task SharedTagRenameKeepsAssignmentsAndDeletionChecksRangesAndScenesInOtherRoots()
    {
        using var store = OpenStore();
        var profile = await AddProfileAsync(store, "first");
        var other = await AddProfileAsync(store, "second");
        var main = await store.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, Main);
        var alternate = await store.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, Alternate);
        var otherTag = await store.GetOrCreateScenarioGameTagAsync(other.Id, "共通");
        var otherVersion = await store.GetScenarioMaterialVersionAsync(other.Id);
        var mainItem = await RecordAsync(store, main, "「共通タグを付ける本文」");
        var alternateItem = await RecordAsync(store, alternate, "「別枝の本文」");
        var alternateVersion = (await store.GetScenarioMaterialAsync(alternate, new())).Version;

        var range = await SaveRangeAsync(store, main, mainItem, "共通");
        var fromAlternate = await store.GetScenarioSceneFlowAsync(alternate);
        var tag = Assert.Single(fromAlternate.Tags);
        Assert.Equal("共通", tag.Name);
        Assert.NotEqual(otherTag, tag.Id);
        Assert.Empty(fromAlternate.RangeTags);
        Assert.True(fromAlternate.Version.Revision > alternateVersion.Revision);
        Assert.Equal(alternateVersion.MatchingRevision, fromAlternate.Version.MatchingRevision);
        var alternateRange = await SaveRangeAsync(store, alternate, alternateItem, "共通");
        var scene = await store.CreateScenarioSceneAsync(alternate, Edit("別枝の場面", [alternateRange], [tag.Id]));
        var oldVersion = await store.GetScenarioMaterialVersionAsync(profile.Id);
        var mainVersion = (await store.GetScenarioMaterialAsync(main, new())).Version;
        var alternateBeforeRename = await store.GetScenarioMaterialVersionAsync(alternate);
        await store.RenameScenarioGameTagAsync(alternate, tag.Id, "改名");
        Assert.Equal(mainVersion.Revision + 1, (await store.GetScenarioMaterialVersionAsync(main)).Revision);
        Assert.Equal(mainVersion.MatchingRevision, (await store.GetScenarioMaterialAsync(main, new())).Version.MatchingRevision);
        Assert.Equal(oldVersion.Revision + 1, (await store.GetScenarioMaterialVersionAsync(profile.Id)).Revision);
        Assert.Equal(new ScenarioGameTag(tag.Id, "改名"), Assert.Single((await store.GetScenarioSceneFlowAsync(profile.Id)).Tags));
        Assert.Equal(new[] { "改名" }, Assert.Single(await store.GetCaptureRangesAsync(main)).Tags);
        var withAssignments = await store.GetScenarioSceneFlowAsync(alternate);
        Assert.Equal(alternateBeforeRename with { Revision = alternateBeforeRename.Revision + 1 }, withAssignments.Version);
        Assert.Equal(new ScenarioRangeTagLink(alternateRange, tag.Id), Assert.Single(withAssignments.RangeTags));
        Assert.Equal(tag.Id, Assert.Single(Assert.Single(withAssignments.Scenes).Definition.TagIds));
        var mainBeforeRejection = await store.GetScenarioMaterialVersionAsync(main);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScenarioGameTagAsync(alternate, tag.Id)); // main range
        Assert.Equal(withAssignments.Version, await store.GetScenarioMaterialVersionAsync(alternate));
        Assert.Equal(mainBeforeRejection, await store.GetScenarioMaterialVersionAsync(main));

        await store.UpdateCaptureRangeAsync(alternate, alternateRange, "本文", []);
        var sceneOnly = await store.GetScenarioSceneFlowAsync(alternate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteScenarioGameTagAsync(main, tag.Id)); // alternate scene only
        Assert.Equal(sceneOnly.Version, await store.GetScenarioMaterialVersionAsync(alternate));
        Assert.Equal(tag.Id, Assert.Single(Assert.Single(sceneOnly.Scenes).Definition.TagIds));
        Assert.Equal(mainBeforeRejection, await store.GetScenarioMaterialVersionAsync(main));
        await store.UpdateScenarioSceneAsync(alternate, scene, Edit("別枝の場面", [alternateRange]));
        var beforeDeletion = await store.GetScenarioMaterialVersionAsync(alternate);
        await store.DeleteScenarioGameTagAsync(main, tag.Id);
        Assert.Empty(Assert.Single(await store.GetCaptureRangesAsync(main), x => x.Id == range).Tags);
        Assert.Empty((await store.GetScenarioSceneFlowAsync(alternate)).Tags);
        Assert.Equal(beforeDeletion.Revision + 1, (await store.GetScenarioMaterialVersionAsync(alternate)).Revision);
        Assert.Equal(otherVersion, await store.GetScenarioMaterialVersionAsync(other.Id));
        Assert.Equal(new ScenarioGameTag(otherTag, "共通"), Assert.Single((await store.GetScenarioSceneFlowAsync(other.Id)).Tags));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "親の分類と題名を子で使い、ラベル名変更ではそのラベルが有効な記録範囲の資料版だけを進める"
    // oracle = { type = "contract", ref = "IScenarioCaptureStore.GetCaptureLabelsAsync; Session Graph play-recording-web: scenario-branch-record-vs-profile; docs/design/scenario-extraction.md: 手動スクリーンショットと分析用zip" }
    // fault = "枝に分類を複製する、他作品の分類を適用する、ラベル名を変更しても参照資料を失効させない、またはZIPに子レコード内部の空の題名を出す"
    // observable = "親と子の分類ID・更新後分類名、適用拒否、参照有無で分かれる資料版、ZIP manifestの作品タイトルと記録ID"
    // observation_boundary = "public-boundary"
    // scope = "SQLite shared classification and archive profile metadata"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task BranchUsesParentLabelsAndArchiveTitleWithoutInvalidatingAnUnusedClassification()
    {
        using var store = OpenStore();
        var profile = await AddProfileAsync(store, "first");
        var other = await AddProfileAsync(store, "second");
        var main = await store.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, Main);
        var alternate = await store.GetOrCreateBranchRecordIdAsync(profile.Id, Repo, Alternate);
        var label = Assert.Single(await store.GetCaptureLabelsAsync(profile.Id), x => x.Kind == CaptureKind.Dialogue);
        Assert.Contains(label, await store.GetCaptureLabelsAsync(main));
        var item = await RecordAsync(store, main, "「台詞」");
        Assert.Equal(label.Id, item.LabelId);
        var unclassified = await RecordAsync(store, alternate, "括弧のない本文");
        Assert.Null(unclassified.LabelId);
        var otherLabel = Assert.Single(await store.GetCaptureLabelsAsync(other.Id), x => x.Kind == CaptureKind.Dialogue);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetCaptureLabelsAsync(item.SessionId, [item.Id], otherLabel.Id));
        var before = (await store.GetScenarioMaterialAsync(main, new())).Version;
        var untouched = await store.GetScenarioMaterialVersionAsync(alternate);
        var legacy = await store.GetScenarioMaterialVersionAsync(profile.Id);
        await ExecuteAsync("UPDATE CaptureLabels SET Name='会話' WHERE Id=$id", label.Id);
        var after = await store.GetScenarioMaterialAsync(main, new());
        Assert.Equal("会話", Assert.Single(after.Occurrences).LabelName);
        Assert.Equal(before.Revision + 1, after.Version.Revision);
        Assert.Equal(before.MatchingRevision, after.Version.MatchingRevision);
        Assert.Equal(untouched, await store.GetScenarioMaterialVersionAsync(alternate));
        Assert.Equal(legacy, await store.GetScenarioMaterialVersionAsync(profile.Id));

        await store.UpsertAsync(new("first", "改名した作品", "synthetic.exe", null, null, null, GameCardStatus.Unknown));
        Assert.Equal(after.Version.Revision + 1, (await store.GetScenarioMaterialVersionAsync(main)).Revision);
        var zipPath = Path.Combine(_directory, "synthetic.zip");
        await store.ExportScenarioArchiveAsync(main, new(), zipPath, ScenarioMaterialAccessLevel.Structure);
        using var zip = ZipFile.OpenRead(zipPath);
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        var game = manifest.RootElement.GetProperty("game");
        Assert.Equal("改名した作品", game.GetProperty("title").GetString());
        Assert.Equal(main.ToString(System.Globalization.CultureInfo.InvariantCulture), game.GetProperty("captureGameId").GetString());
    }

    private SqliteGameLibraryStore OpenStore()
    {
        Directory.CreateDirectory(_directory);
        return new(NullLogger<SqliteGameLibraryStore>.Instance, Database);
    }

    private static async Task<CaptureProfile> AddProfileAsync(SqliteGameLibraryStore store, string id)
    {
        await store.UpsertAsync(new(id, id, "synthetic.exe", null, null, null, GameCardStatus.Unknown));
        return await store.GetOrCreateCaptureProfileAsync(id);
    }

    private static async Task<CaptureTextItem> RecordAsync(SqliteGameLibraryStore store, long recordId, string text, string? speaker = null)
    {
        var session = await store.BeginCaptureSessionAsync(recordId);
        Assert.True(await store.AppendCaptureAsync(session, new(1, Time, Source, text, CaptureRole.Body), new()));
        if (speaker is not null)
            await store.SetCaptureSpeakerAsync(session, Assert.Single(await store.GetCaptureItemsAsync(session)).Id, speaker);
        await store.EndCaptureSessionAsync(session);
        return Assert.Single(await store.GetCaptureItemsAsync(session));
    }

    private static Task<long> SaveRangeAsync(SqliteGameLibraryStore store, long id, CaptureTextItem text, params string[] tags) =>
        store.SaveCaptureRangeAsync(id, "本文", text.StartPosition, text.EndPosition, tags);

    private static Task<ScenarioScreenshot> SaveImageAsync(SqliteGameLibraryStore store, long recordId, CaptureEventKey key) =>
        store.SaveScreenshotAsync(new(recordId, key), new(1, 1, new string('a', 64), Png), Time);

    private static ScenarioSceneEdit Edit(string title, long[] ranges, long[]? tags = null) => new(title, null, null, 0, 0, ranges, tags ?? []);

    private async Task ExecuteAsync(string sql, long? id = null)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, ForeignKeys = true }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id.Value);
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
