using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Services;
using GameLauncherWithGit.Infrastructure.Abstractions;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(), "capture-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteGameLibraryStore _store;
    private static readonly CaptureSource Source = new(10, 100, 200, 0, "HQ@game", "body");
    public ScenarioCaptureTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
    }
    private async Task<(CaptureProfile Profile, long Session)> StartAsync()
    {
        await _store.UpsertAsync(new("game", "作品", "game.exe", null, null, null, GameCardStatus.Unknown));
        var profile = await _store.GetOrCreateCaptureProfileAsync("game");
        return (profile, await _store.BeginCaptureSessionAsync(profile.Id));
    }
    private static CaptureReceived Receive(long sequence, string text, CaptureRole role = CaptureRole.Body, DateTimeOffset? time = null) =>
        new(sequence, time ?? DateTimeOffset.UtcNow, Source, text, role);

    // @test-value v2
    // kind = "invariant"
    // claim = "混在本文は原文を欠かさないUTF-16範囲に分割される"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#原文と分類" }
    // fault = "括弧外の文字を落とす、またはサロゲート途中を分割位置として許す"
    // observable = "各範囲を結合した文字列、種別、境界検証の例外"
    // observation_boundary = "component-behavior"
    // scope = "scenario text processing"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void SplitPreservesMixedTextAndUtf16Boundaries()
    {
        const string raw = "朝🌅「おはよう『友人』」「またね」余韻";
        var parts = ScenarioTextProcessor.Split(raw, new() { OutsideIsNarration = true });
        Assert.Equal(raw, string.Concat(parts.Select(x => raw.Substring(x.Start, x.Length))));
        Assert.Equal(new CaptureKind?[] { CaptureKind.Narration, CaptureKind.Dialogue, CaptureKind.Dialogue, CaptureKind.Narration }, parts.Select(x => x.Kind));
        Assert.Throws<ArgumentException>(() => ScenarioTextProcessor.ValidateBoundaries(raw, [2]));
        ScenarioTextProcessor.ValidateBoundaries(raw, [3]);
        Assert.Null(Assert.Single(ScenarioTextProcessor.Split("「未完", new() { OutsideIsNarration = true })).Kind);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "同じ受信の再投入だけを抑え同文の別受信と元の順序を保存する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#受信と寿命" }
    // fault = "本文一致で反復を消す、またはDB挿入順で本文を並べる"
    // observable = "保存結果の連番と本文、再投入時の追加結果"
    // observation_boundary = "public-boundary"
    // scope = "SQLite capture append/read"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task AppendKeepsRepeatedTextAndReceiveOrder()
    {
        var (_, session) = await StartAsync();
        Assert.True(await _store.AppendCaptureAsync(session, Receive(2, "「同文」"), new()));
        Assert.True(await _store.AppendCaptureAsync(session, Receive(1, "「同文」"), new()));
        Assert.False(await _store.AppendCaptureAsync(session, Receive(1, "「同文」"), new()));
        var items = await _store.GetCaptureItemsAsync(session);
        Assert.Equal(new long[] { 1, 2 }, items.Select(x => x.Sequence));
        Assert.All(items, x => Assert.Equal("「同文」", x.Text));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ラベル改名は過去へ反映し再適用は手動ラベルと話者と分割を維持する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#修正と再適用" }
    // fault = "ラベル名をitemへ複製する、または再適用で手動値や境界を置換する"
    // observable = "再読込したitemのラベル名・話者・ID・本文とTXT"
    // observation_boundary = "public-boundary"
    // scope = "SQLite reclassification and TXT"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task RenameAndReapplyPreserveManualEdits()
    {
        var (profile, session) = await StartAsync();
        await _store.AppendCaptureAsync(session, Receive(1, "地の文「発言」"), new() { OutsideIsNarration = true });
        var items = await _store.GetCaptureItemsAsync(session);
        var dialogue = Assert.Single(items, x => x.Kind == CaptureKind.Dialogue);
        var label = Assert.Single(await _store.GetCaptureLabelsAsync(profile.Id), x => x.Kind == CaptureKind.Dialogue);
        await _store.SetCaptureLabelsAsync(session, [dialogue.Id], label.Id);
        await _store.SetCaptureSpeakerAsync(session, dialogue.Id, "美咲");
        await _store.SaveCaptureLabelAsync(profile.Id, "会話", CaptureKind.Dialogue, label.Id);
        await _store.ReapplyCaptureRulesAsync(session, items.Select(x => x.Id).ToArray(), new() { SplitDialogue = false });
        var updated = await _store.GetCaptureItemsAsync(session);
        var spoken = Assert.Single(updated, x => x.Id == dialogue.Id);
        Assert.Equal("会話", spoken.LabelName);
        Assert.Equal("美咲", spoken.Speaker);
        Assert.Equal(items.Select(x => (x.Start, x.Length)), updated.Select(x => (x.Start, x.Length)));
        Assert.Contains("[会話：美咲]", ScenarioTextProcessor.FormatTxt(updated));
        Assert.Equal("地の文「発言」", string.Concat(updated.Select(x => x.Text)));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "登録削除後の履歴は残り同一ID再登録へ自動統合されない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#保存と登録削除" }
    // fault = "Gamesの削除で履歴を消す、または同じslugへの再登録で旧所有者を再利用する"
    // observable = "削除後の原文・旧所有者GameId・新旧所有者ID"
    // observation_boundary = "public-boundary"
    // scope = "game deletion and capture ownership"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task DeleteAndRegisterAgainKeepIndependentHistory()
    {
        var (profile, session) = await StartAsync();
        await _store.AppendCaptureAsync(session, Receive(1, "保存済み"), new());
        await _store.DeleteAsync("game");
        var archived = Assert.Single(await _store.GetCaptureProfilesAsync());
        Assert.Null(archived.GameId);
        Assert.Equal("保存済み", Assert.Single(await _store.GetCaptureItemsAsync(session)).Text);
        var (newProfile, _) = await StartAsync();
        Assert.NotEqual(profile.Id, newProfile.Id);
        Assert.NotEqual(session, Assert.Single(await _store.GetCaptureSessionsAsync(newProfile.Id)).Id);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "名前の後着を許し曖昧な複数発言へ名前を配らない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#原文と分類" }
    // fault = "直前名だけを貼る、窓が閉じる前に確定する、または同じ名を複数発言に使う"
    // observable = "話者照合結果のID別名前値"
    // observation_boundary = "component-behavior"
    // scope = "speaker matching"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void MatchWaitsForLateNameAndRejectsAmbiguity()
    {
        var time = DateTimeOffset.UtcNow;
        var item = new CaptureTextItem(1, 1, 1, time, "「発言」", 0, 4, 1, "セリフ", CaptureKind.Dialogue, null, false, false, false);
        CaptureReceived[] events = [Receive(1, item.Text, time: time), Receive(2, "美咲", CaptureRole.Name, time.AddMilliseconds(400))];
        Assert.Empty(ScenarioTextProcessor.MatchSpeakers(events, [item], ["美咲"], time.AddMilliseconds(500)));
        Assert.Equal("美咲", ScenarioTextProcessor.MatchSpeakers(events, [item], ["美咲"], time.AddSeconds(3))[1]);
        var second = item with { Id = 2, Sequence = 3, ReceivedAt = time.AddMilliseconds(700) };
        var ambiguous = ScenarioTextProcessor.MatchSpeakers(events, [item, second], ["美咲"], time.AddSeconds(3));
        Assert.Equal(new long[] { 1, 2 }, ambiguous.Keys.Order());
        Assert.All(ambiguous.Values, value => Assert.Null(value));
        Assert.Null(ScenarioTextProcessor.MatchSpeakers([events[0], events[1] with { Text = "美咲寿季" }], [item], ["美咲", "寿季"], time.AddSeconds(3))[1]);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "明示再適用は選択範囲の自動話者だけを再計算し手動未確定と他範囲を維持する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#修正と再適用" }
    // fault = "名前ルール変更を全履歴へ適用する、または手動NULLを自動値で置換する"
    // observable = "再適用後の選択・非選択itemの話者と手動分割した原文"
    // observation_boundary = "public-boundary"
    // scope = "SQLite selected reapply and manual partition"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task SelectedReapplyAndManualPartitionKeepTheirBoundaries()
    {
        var (profile, session) = await StartAsync();
        var time = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _store.AppendCaptureAsync(session, Receive(1, "「最初」", time: time), new());
        await _store.AppendCaptureAsync(session, Receive(2, "美咲", CaptureRole.Name, time.AddMilliseconds(200)), new());
        await _store.AppendCaptureAsync(session, Receive(3, "「次」", time: time.AddSeconds(5)), new());
        await _store.AppendCaptureAsync(session, Receive(4, "美咲", CaptureRole.Name, time.AddSeconds(5.2)), new());
        await _store.ResolveCaptureSpeakersAsync(session, new() { Names = ["美咲"] }, time, DateTimeOffset.UtcNow);
        var before = await _store.GetCaptureItemsAsync(session);
        Assert.All(before, x => Assert.Equal("美咲", x.Speaker));
        await _store.ReapplyCaptureRulesAsync(session, [before[0].Id], new());
        var after = await _store.GetCaptureItemsAsync(session);
        Assert.Null(after[0].Speaker);
        Assert.Equal("美咲", after[1].Speaker);
        await _store.SetCaptureLabelsAsync(session, [after[1].Id], null);
        await _store.SetCaptureSpeakerAsync(session, after[1].Id, null);
        await _store.ReapplyCaptureRulesAsync(session, [after[1].Id], new() { Names = ["美咲"] });
        var intentional = Assert.Single(await _store.GetCaptureItemsAsync(session), x => x.Id == after[1].Id);
        Assert.Null(intentional.LabelId); Assert.Null(intentional.Speaker);
        var dialogue = Assert.Single(await _store.GetCaptureLabelsAsync(profile.Id), x => x.Kind == CaptureKind.Dialogue);
        await _store.SplitCaptureEventAsync(session, 3, [1], [new(null, null), new(dialogue.Id, "手動名")]);
        var manual = (await _store.GetCaptureItemsAsync(session)).Where(x => x.Sequence == 3).ToArray();
        await _store.ReapplyCaptureRulesAsync(session, manual.Select(x => x.Id).ToArray(), new() { SplitDialogue = false, Names = ["美咲"] });
        var final = (await _store.GetCaptureItemsAsync(session)).Where(x => x.Sequence == 3).ToArray();
        Assert.Equal(manual.Select(x => (x.Id, x.Start, x.Length)), final.Select(x => (x.Id, x.Start, x.Length)));
        Assert.Null(final[0].LabelId); Assert.Null(final[0].Speaker);
        Assert.Equal("手動名", final[1].Speaker);
        Assert.Equal("「次」", string.Concat(final.Select(x => x.Text)));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "OFF起動は接続と新規履歴を作らずONの候補再選択は同一受信を二重保存しない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#受信と寿命" }
    // fault = "OFFでも本体選択を要求する、または選択前bufferを再選択のたびに保存する"
    // observable = "接続要求数、プレイ回件数、元の受信順で保存された本文と終了後の本体生存"
    // observation_boundary = "public-boundary"
    // scope = "capture manager lifecycle and finite candidate replay"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ManagerOptOutAndCandidateReplayDoNotInterfereWithBody()
    {
        var (profile, _) = await StartAsync();
        var sessions = (await _store.GetCaptureSessionsAsync(profile.Id)).Count;
        await _store.SaveCaptureProfileAsync(profile with { Enabled = false });
        var host = new FakeHost();
        await using var manager = new ScenarioCaptureService(_store, host, NullLogger<ScenarioCaptureService>.Instance);
        Assert.False(await manager.BeginGameAsync("game"));
        Assert.False(manager.Snapshot.NeedsProcess);
        Assert.Equal(0, host.Connections);
        Assert.Equal(sessions, (await _store.GetCaptureSessionsAsync(profile.Id)).Count);
        await _store.SaveCaptureProfileAsync(profile);
        Assert.True(await manager.BeginGameAsync("game"));
        await manager.SelectProcessAsync(Environment.ProcessId);
        var source = Source with { ProcessId = (uint)Environment.ProcessId };
        host.Emit(source, "「選択前」");
        var selected = new CaptureRules { Selections = [new(source.HookCode, source.HookName, source.Context, source.Context2, CaptureRole.Body)] };
        await manager.SaveRulesAsync(selected);
        host.Emit(source, "「選択後」");
        await manager.SaveRulesAsync(new());
        await manager.SaveRulesAsync(selected);
        await manager.FinishAsync();
        var items = await _store.GetCaptureItemsAsync(manager.Snapshot.SessionId!.Value);
        Assert.Equal(new[] { "「選択前」", "「選択後」" }, items.Select(x => x.Text));
        Assert.Equal(new long[] { 1, 2 }, items.Select(x => x.Sequence));
        Assert.Equal(1, host.Connections); Assert.Equal(1, host.Detaches);
        Assert.NotNull((await _store.GetCaptureSessionsAsync(profile.Id))[0].EndedAt);
        using var body = Process.GetCurrentProcess();
        Assert.False(body.HasExited);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "名前ルール変更は判定待ちの既受信本文へ遡及せず受信時ルールで話者を確定する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#修正と再適用" }
    // fault = "周期照合が最新の名前一覧で保存済み本文を再評価する"
    // observable = "ルール変更前に届いた本文と名前が保存時設定で対応した話者"
    // observation_boundary = "public-boundary"
    // scope = "capture manager pending speaker matching"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task PendingSpeakerMatchingUsesReceivedRules()
    {
        await StartAsync();
        var host = new FakeHost();
        await using var manager = new ScenarioCaptureService(_store, host, NullLogger<ScenarioCaptureService>.Instance);
        await manager.BeginGameAsync("game");
        await manager.SelectProcessAsync(Environment.ProcessId);
        var body = Source with { ProcessId = (uint)Environment.ProcessId };
        var name = body with { HookName = "name", Address = 101 };
        var rules = new CaptureRules { Names = ["美咲"], Selections = [
            new(body.HookCode, body.HookName, body.Context, body.Context2, CaptureRole.Body),
            new(name.HookCode, name.HookName, name.Context, name.Context2, CaptureRole.Name)] };
        await manager.SaveRulesAsync(rules);
        host.Emit(body, "「設定変更前の発言」"); host.Emit(name, "美咲");
        await manager.SaveRulesAsync(rules with { Names = [] });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        CaptureTextItem? item;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        do
        {
            await timer.WaitForNextTickAsync();
            item = (await _store.GetCaptureItemsAsync(manager.Snapshot.SessionId!.Value)).SingleOrDefault();
        } while (item?.Speaker is null && DateTimeOffset.UtcNow < deadline);
        Assert.Equal("美咲", item?.Speaker);
    }

    private sealed class FakeHost : ILunaCaptureHost
    {
        public event Action<CaptureSource, string>? Output;
        public event Action<uint, bool>? ConnectionChanged;
        public int Connections, Detaches;
        public Task ConnectAsync(int processId) { Connections++; ConnectionChanged?.Invoke((uint)processId, true); return Task.CompletedTask; }
        public void Detach(int processId) { Detaches++; ConnectionChanged?.Invoke((uint)processId, false); }
        public IReadOnlyList<CaptureProcess> GetProcesses() => [new(Environment.ProcessId, "test-body", Environment.ProcessPath!)];
        public void Emit(CaptureSource source, string text) => Output?.Invoke(source, text);
    }

    // @test-value v2
    // kind = "compatibility"
    // claim = "抽出テーブルの追加初期化は既存のゲーム・セーブリンク・同期履歴を保持する"
    // oracle = { type = "contract", ref = "README.md#互換性とデータ保護" }
    // fault = "新機能の初期化で既存テーブルや行を作り直す"
    // observable = "初期化前後のゲーム・リンク・履歴の公開store読出し値"
    // observation_boundary = "public-boundary"
    // scope = "existing SQLite schema initialization"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task CaptureSchemaAdditionPreservesExistingLibraryData()
    {
        await StartAsync();
        var game = (await _store.FindByIdAsync("game"))! with { RelatedRepositoryPath = "C:\\repository", IsPinned = true };
        GameSaveLinkItem[] links = [new("link", "game", "セーブ", "C:\\local", "C:\\target", 0, true)];
        await _store.UpsertWithSaveLinksAsync(game, links);
        var time = DateTimeOffset.UtcNow;
        await _store.AppendAsync(new(0, "repo", RepositorySyncHistoryStatus.Succeeded, time, time, 0, "pull", "fixture"));
        var history = (await _store.GetLatestByRepositoryIdsAsync(["repo"], 10))["repo"];
        // Remove only capture tables in this test-owned database to model the deployed pre-capture schema.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys=OFF;
                DROP TABLE CaptureItems; DROP TABLE CaptureEvents; DROP TABLE CaptureSources;
                DROP TABLE CaptureSessions; DROP TABLE CaptureLabels; DROP TABLE CaptureGames;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var upgraded = new SqliteGameLibraryStore(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
        var profile = await upgraded.GetOrCreateCaptureProfileAsync("game");
        Assert.Equal(game, await upgraded.FindByIdAsync("game"));
        Assert.Equal(links, await upgraded.GetByGameIdAsync("game"));
        Assert.Equal(history, (await upgraded.GetLatestByRepositoryIdsAsync(["repo"], 10))["repo"]);
        Assert.Equal(3, (await upgraded.GetCaptureLabelsAsync(profile.Id)).Count);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
