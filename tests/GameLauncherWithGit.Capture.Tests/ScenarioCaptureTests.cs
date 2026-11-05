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
    // claim = "保存済みの分類名を保持し再適用は手動分類と話者と分割を維持する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#修正と再適用" }
    // fault = "保存済みの独自分類名を固定名へ書き換える、または再適用で手動値や境界を置換する"
    // observable = "再読込したitemのラベル名・話者・ID・本文とTXT"
    // observation_boundary = "public-boundary"
    // scope = "SQLite reclassification and TXT"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ExistingClassificationAndReapplyPreserveManualEdits()
    {
        var (profile, session) = await StartAsync();
        await _store.AppendCaptureAsync(session, Receive(1, "地の文「発言」"), new() { OutsideIsNarration = true });
        var items = await _store.GetCaptureItemsAsync(session);
        var dialogue = Assert.Single(items, x => x.Kind == CaptureKind.Dialogue);
        var label = Assert.Single(await _store.GetCaptureLabelsAsync(profile.Id), x => x.Kind == CaptureKind.Dialogue);
        await _store.SetCaptureLabelsAsync(session, [dialogue.Id], label.Id);
        await _store.SetCaptureSpeakerAsync(session, dialogue.Id, "美咲");
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE CaptureLabels SET Name='会話' WHERE Id=$id";
            command.Parameters.AddWithValue("$id", label.Id);
            await command.ExecuteNonQueryAsync();
        }
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
    // claim = "登録なしで名前取得元の単一行を使い、後着を待ち、空・複数行・曖昧な対応を確定しない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#原文と分類" }
    // fault = "登録を要求する、名前以外の取得元を使う、空・複数行を採用する、または同じ名を複数発言に使う"
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
        CaptureReceived[] events = [Receive(1, item.Text, time: time), Receive(2, " 美咲 ", CaptureRole.Name, time.AddMilliseconds(400))];
        Assert.Empty(ScenarioTextProcessor.MatchSpeakers(events, [item], time.AddMilliseconds(500)));
        Assert.Equal("美咲", ScenarioTextProcessor.MatchSpeakers(events, [item], time.AddSeconds(3))[1]);
        var second = item with { Id = 2, Sequence = 3, ReceivedAt = time.AddMilliseconds(700) };
        var ambiguous = ScenarioTextProcessor.MatchSpeakers(events, [item, second], time.AddSeconds(3));
        Assert.Equal(new long[] { 1, 2 }, ambiguous.Keys.Order());
        Assert.All(ambiguous.Values, value => Assert.Null(value));
        foreach (var text in new[] { " ", "美咲\n寿季", "美咲\r寿季" })
            Assert.Null(ScenarioTextProcessor.MatchSpeakers([events[0], events[1] with { Text = text }], [item], time.AddSeconds(3))[1]);
        Assert.Null(ScenarioTextProcessor.MatchSpeakers([events[0], events[1] with { Role = CaptureRole.None }], [item], time.AddSeconds(3))[1]);
        Assert.Null(ScenarioTextProcessor.MatchSpeakers([.. events, events[1] with { Sequence = 3, Text = "寿季" }], [item], time.AddSeconds(3))[1]);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "明示再適用は選択範囲の自動話者だけを再計算し手動未確定と他範囲を維持する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#修正と再適用" }
    // fault = "再適用を全履歴へ広げる、または手動NULLを自動値で置換する"
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
        await _store.AppendCaptureAsync(session, Receive(3, "「次」", time: time.AddSeconds(5)), new());
        await _store.AppendCaptureAsync(session, Receive(4, "美咲", CaptureRole.Name, time.AddSeconds(5.2)), new());
        await _store.ResolveCaptureSpeakersAsync(session, time, DateTimeOffset.UtcNow);
        var before = await _store.GetCaptureItemsAsync(session);
        Assert.Null(before[0].Speaker);
        Assert.Equal("美咲", before[1].Speaker);
        await _store.AppendCaptureAsync(session, Receive(2, "寿季", CaptureRole.Name, time.AddMilliseconds(200)), new());
        await _store.AppendCaptureAsync(session, Receive(5, "別の名前", CaptureRole.Name, time.AddSeconds(5.3)), new());
        await _store.ReapplyCaptureRulesAsync(session, [before[0].Id], new());
        var after = await _store.GetCaptureItemsAsync(session);
        Assert.Equal("寿季", after[0].Speaker);
        Assert.Equal("美咲", after[1].Speaker);
        await _store.SetCaptureLabelsAsync(session, [after[0].Id], null);
        await _store.SetCaptureSpeakerAsync(session, after[0].Id, null);
        await _store.ReapplyCaptureRulesAsync(session, [after[0].Id], new());
        var intentional = Assert.Single(await _store.GetCaptureItemsAsync(session), x => x.Id == after[0].Id);
        Assert.Null(intentional.LabelId); Assert.Null(intentional.Speaker);
        var dialogue = Assert.Single(await _store.GetCaptureLabelsAsync(profile.Id), x => x.Kind == CaptureKind.Dialogue);
        await _store.SplitCaptureEventAsync(session, 3, [1], [new(null, null), new(dialogue.Id, "手動名")]);
        var manual = (await _store.GetCaptureItemsAsync(session)).Where(x => x.Sequence == 3).ToArray();
        await _store.ReapplyCaptureRulesAsync(session, manual.Select(x => x.Id).ToArray(), new() { SplitDialogue = false });
        var final = (await _store.GetCaptureItemsAsync(session)).Where(x => x.Sequence == 3).ToArray();
        Assert.Equal(manual.Select(x => (x.Id, x.Start, x.Length)), final.Select(x => (x.Id, x.Start, x.Length)));
        Assert.Null(final[0].LabelId); Assert.Null(final[0].Speaker);
        Assert.Equal("手動名", final[1].Speaker);
        Assert.Equal("「次」", string.Concat(final.Select(x => x.Text)));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "OFF起動は接続と履歴を作らずテスト受信を破棄し確定後の本文だけを一度保存する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#受信と寿命" }
    // fault = "OFFでも接続する、テスト本文を確定時に保存する、または用途変更で同一受信を二重保存する"
    // observable = "接続要求数、プレイ回件数、元の受信順で保存された本文と終了後の本体生存"
    // observation_boundary = "public-boundary"
    // scope = "capture manager opt-out, preview and confirmation boundary"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ManagerOptOutAndPreviewRespectRecordingBoundary()
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
        await manager.FlushPendingAsync();
        Assert.Null(manager.Snapshot.SessionId);
        Assert.True(manager.Snapshot.Preview);
        Assert.Equal(sessions, (await _store.GetCaptureSessionsAsync(profile.Id)).Count);
        Assert.Null((await _store.GetCaptureProfilesAsync()).Single().BodyExecutablePath);
        await manager.ConfirmProcessAsync();
        Assert.Empty(manager.Snapshot.PreviewItems);
        Assert.All(manager.Snapshot.Candidates, candidate => Assert.Empty(candidate.Samples));
        host.Emit(source, "「選択後」");
        await manager.SaveRulesAsync(new());
        await manager.SaveRulesAsync(selected);
        await manager.FinishAsync();
        var items = await _store.GetCaptureItemsAsync(manager.Snapshot.SessionId!.Value);
        Assert.Equal(new[] { "「選択後」" }, items.Select(x => x.Text));
        Assert.Equal(new long[] { 1 }, items.Select(x => x.Sequence));
        Assert.Equal(1, host.Connections); Assert.Equal(1, host.Detaches);
        Assert.NotNull((await _store.GetCaptureSessionsAsync(profile.Id))[0].EndedAt);
        using var body = Process.GetCurrentProcess();
        Assert.False(body.HasExited);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "名前取得元の指定だけで新しい名前も記録し、受信済み用途と保存済み設定データを保持する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#原文と分類" }
    // fault = "登録外の名前を拒否する、用途変更を過去へ遡及する、または設定保存で名前配列を失う"
    // observable = "別々に受信した発言の話者と再読込した設定の名前配列・取得元"
    // observation_boundary = "public-boundary"
    // scope = "capture manager pending speaker matching"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task SelectedNameSourceAcceptsNewNamesWithoutRegistration()
    {
        await StartAsync();
        var host = new FakeHost();
        await using var manager = new ScenarioCaptureService(_store, host, NullLogger<ScenarioCaptureService>.Instance);
        await manager.BeginGameAsync("game");
        await manager.SelectProcessAsync(Environment.ProcessId);
        var body = Source with { ProcessId = (uint)Environment.ProcessId };
        var name = body with { HookName = "name", Address = 101 };
        var rules = new CaptureRules { Selections = [
            new(body.HookCode, body.HookName, body.Context, body.Context2, CaptureRole.Body),
            new(name.HookCode, name.HookName, name.Context, name.Context2, CaptureRole.Name)] };
        await manager.SaveRulesAsync(rules);
        await manager.ConfirmProcessAsync();
        host.Emit(body, "「設定変更前の発言」"); host.Emit(name, "美咲");
        await manager.SaveRulesAsync(rules with { Selections = [] });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        CaptureTextItem? item;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        do
        {
            await timer.WaitForNextTickAsync();
            item = (await _store.GetCaptureItemsAsync(manager.Snapshot.SessionId!.Value)).SingleOrDefault();
        } while (item?.Speaker is null && DateTimeOffset.UtcNow < deadline);
        Assert.Equal("美咲", item?.Speaker);
        var storedRules = rules with { Names = ["保存済みの名前"], OutsideIsNarration = true };
        await manager.SaveRulesAsync(storedRules);
        host.Emit(body, "「初登場の発言」"); host.Emit(name, "寿季");
        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        do
        {
            await timer.WaitForNextTickAsync();
            item = (await _store.GetCaptureItemsAsync(manager.Snapshot.SessionId!.Value)).LastOrDefault();
        } while (item?.Speaker != "寿季" && DateTimeOffset.UtcNow < deadline);
        Assert.Equal(new[] { "美咲", "寿季" }, (await _store.GetCaptureItemsAsync(manager.Snapshot.SessionId!.Value)).Select(x => x.Speaker));
        var savedRules = Assert.Single(await _store.GetCaptureProfilesAsync()).Rules;
        Assert.Equal(storedRules.Names, savedRules.Names);
        Assert.Equal(rules.Selections, savedRules.Selections);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ゲーム全体の本文とTXTは複数取得回を受信順に含み区間外や他ゲームを混同しない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#ゲーム単位の本文と任意区間" }
    // fault = "最新回だけを読む、受信時刻で順序を変える、または区間作成で範囲外を消す"
    // observable = "全体と区間の本文順、手動話者、ラベル条件、全件とページの件数、再読込した区間"
    // observation_boundary = "public-boundary"
    // scope = "game-wide SQLite read, range and TXT"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task GameLogAndRangeSpanAcquisitionsWithoutRemovingOutsideText()
    {
        var (profile, first) = await StartAsync();
        await _store.AppendCaptureAsync(first, Receive(2, "「一章」"), new());
        await _store.AppendCaptureAsync(first, Receive(1, "範囲の前"), new());
        var old = (await _store.GetCaptureItemsAsync(first))[1];
        await _store.SetCaptureSpeakerAsync(first, old.Id, "手動名");
        await _store.EndCaptureSessionAsync(first);
        var second = await _store.BeginCaptureSessionAsync(profile.Id);
        await _store.AppendCaptureAsync(second, Receive(1, "「二章」", time: DateTimeOffset.UtcNow.AddDays(-1)), new());
        for (var i = 2; i <= 205; i++) await _store.AppendCaptureAsync(second, Receive(i, $"範囲の後{i}"), new());
        await _store.UpsertAsync(new("other", "別作品", "other.exe", null, null, null, GameCardStatus.Unknown));
        var other = await _store.GetOrCreateCaptureProfileAsync("other");
        var otherSession = await _store.BeginCaptureSessionAsync(other.Id);
        await _store.AppendCaptureAsync(otherSession, Receive(1, "別作品の本文"), new());
        Assert.Empty(await _store.GetCaptureRangesAsync(profile.Id));
        var beforeRangeSchema = await _store.GetGameCaptureItemsAsync(profile.Id, 0);
        // Model an existing capture database without the additive range table, using only this test's database.
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE CaptureRanges";
            await command.ExecuteNonQueryAsync();
        }
        var upgraded = new SqliteGameLibraryStore(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
        Assert.Equal(beforeRangeSchema, await upgraded.GetGameCaptureItemsAsync(profile.Id, 0));
        Assert.Empty(await upgraded.GetCaptureRangesAsync(profile.Id));
        var end = (await _store.GetCaptureItemsAsync(second))[0].EndPosition;
        var id = await _store.SaveCaptureRangeAsync(profile.Id, " 自由な章名 ", old.StartPosition, end);
        await _store.SaveCaptureRangeAsync(profile.Id, "自由な章名", new(first, 0, 0), end);
        var all = await _store.GetGameCaptureItemsAsync(profile.Id, 0);
        Assert.Equal(207, all.Count);
        Assert.Equal(new[] { "範囲の前", "「一章」", "「二章」" }, all.Take(3).Select(x => x.Text));
        Assert.Equal(200, (await _store.GetGameCaptureItemsAsync(profile.Id)).Count);
        Assert.Equal(all.Skip(7).Select(x => x.Id), (await _store.GetGameCaptureItemsAsync(profile.Id, latest: true)).Select(x => x.Id));
        Assert.Equal(all.Skip(200).Select(x => x.Id), (await _store.GetGameCaptureItemsAsync(profile.Id, offset: 200)).Select(x => x.Id));
        var range = await _store.GetGameCaptureItemsAsync(profile.Id, 0, rangeId: id);
        Assert.Equal(new[] { "「一章」", "「二章」" }, range.Select(x => x.Text));
        Assert.Equal("手動名", range[0].Speaker);
        var dialogue = Assert.Single(await _store.GetCaptureLabelsAsync(profile.Id), x => x.Kind == CaptureKind.Dialogue);
        Assert.Equal(range.Select(x => x.Id), (await _store.GetGameCaptureItemsAsync(profile.Id, 0, labelId: dialogue.Id, rangeId: id)).Select(x => x.Id));
        var narration = Assert.Single(await _store.GetCaptureLabelsAsync(profile.Id), x => x.Kind == CaptureKind.Narration);
        Assert.Empty(await _store.GetGameCaptureItemsAsync(profile.Id, 0, labelId: narration.Id, rangeId: id));
        var txt = ScenarioTextProcessor.FormatTxt(range);
        Assert.Contains("[セリフ：手動名]", txt); Assert.Contains("「二章」", txt);
        Assert.DoesNotContain("範囲の", txt); Assert.DoesNotContain("別作品", txt);
        Assert.Equal(205, (await _store.GetGameCaptureItemsAsync(profile.Id, 0, unclassifiedOnly: true)).Count);
        var reopened = new SqliteGameLibraryStore(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
        Assert.Equal(2, (await reopened.GetCaptureRangesAsync(profile.Id)).Count);
        Assert.All(await reopened.GetCaptureRangesAsync(profile.Id), x => Assert.Equal("自由な章名", x.Name));
        Assert.Equal(all, await reopened.GetGameCaptureItemsAsync(profile.Id, 0));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "区間は可変item IDではなく原文位置に結び付き再分割後も同じ文字だけを表示し出力する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#ゲーム単位の本文と任意区間" }
    // fault = "item再生成で区間が消える、または再分割後のitem全体へ範囲を広げる"
    // observable = "item ID変更後の区間の文字列とTXT、範囲外を含む全体原文"
    // observation_boundary = "public-boundary"
    // scope = "raw-position range persistence and clipping"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task RangeKeepsExactRawPositionsAfterRepartition()
    {
        var (profile, session) = await StartAsync();
        await _store.AppendCaptureAsync(session, Receive(1, "前ABC後"), new());
        var original = Assert.Single(await _store.GetGameCaptureItemsAsync(profile.Id));
        var id = await _store.SaveCaptureRangeAsync(profile.Id, "中間", new(session, 1, 1), new(session, 1, 4));
        await _store.SplitCaptureEventAsync(session, 1, [2], [new(null, null), new(null, null)]);
        var parts = await _store.GetGameCaptureItemsAsync(profile.Id, 0, rangeId: id);
        Assert.DoesNotContain(parts, x => x.Id == original.Id);
        Assert.Equal(new[] { "A", "BC" }, parts.Select(x => x.Text));
        Assert.Equal("前ABC後", string.Concat((await _store.GetGameCaptureItemsAsync(profile.Id)).Select(x => x.Text)));
        Assert.Equal("[未判定]" + Environment.NewLine + "A" + Environment.NewLine + Environment.NewLine
            + "[未判定]" + Environment.NewLine + "BC" + Environment.NewLine, ScenarioTextProcessor.FormatTxt(parts));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "区間の保存は同じゲームの有効な文字境界と非空範囲と名前を検証し失敗を保存済みにしない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#ゲーム単位の本文と任意区間" }
    // fault = "他ゲームや未保存本文や文字途中の境界を受理する、または失敗時に部分区間を残す"
    // observable = "無効指定の例外と区間0件、元の本文の保持、別ゲームからの範囲読出し拒否"
    // observation_boundary = "public-boundary"
    // scope = "range write and owner validation"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task InvalidRangeDoesNotWriteOrChangeText()
    {
        var (profile, session) = await StartAsync();
        await _store.AppendCaptureAsync(session, Receive(1, "🌅e\u0301朝"), new());
        var item = Assert.Single(await _store.GetGameCaptureItemsAsync(profile.Id));
        await _store.UpsertAsync(new("other", "別", "other.exe", null, null, null, GameCardStatus.Unknown));
        var other = await _store.GetOrCreateCaptureProfileAsync("other");
        var otherSession = await _store.BeginCaptureSessionAsync(other.Id);
        await _store.AppendCaptureAsync(otherSession, Receive(1, "別"), new());
        foreach (var bad in new[] { new CapturePosition(session, 1, 1), new(session, 1, 3), new(session, 9, 0), new(otherSession, 1, 0) })
            await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveCaptureRangeAsync(profile.Id, "章", bad, item.EndPosition));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveCaptureRangeAsync(profile.Id, "章", item.StartPosition, new(otherSession, 1, 1)));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveCaptureRangeAsync(profile.Id, "章", item.EndPosition, item.StartPosition));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveCaptureRangeAsync(profile.Id, "章", item.EndPosition, item.EndPosition));
        foreach (var name in new[] { " ", "章\n名" })
            await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveCaptureRangeAsync(profile.Id, name, item.StartPosition, item.EndPosition));
        Assert.Empty(await _store.GetCaptureRangesAsync(profile.Id));
        Assert.Equal(item, Assert.Single(await _store.GetGameCaptureItemsAsync(profile.Id)));
        var id = await _store.SaveCaptureRangeAsync(profile.Id, "章", item.StartPosition, item.EndPosition);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetGameCaptureItemsAsync(other.Id, rangeId: id));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "プレイ中の区間は開始後から終了までの受理済み本文に固定され取消や未完了終了でも本文保存を止めない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#ゲーム単位の本文と任意区間" }
    // fault = "開始前や終了後を区間に混ぜる、未完了を自動保存する、または取消で原文まで捨てる"
    // observable = "固定された区間本文、全体本文、二重開始と空終了の拒否、取消と終了後のdraft状態"
    // observation_boundary = "public-boundary"
    // scope = "capture service live range and writer barrier"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task LiveRangeFixesReceivedPositionsWithoutControllingCapture()
    {
        var (profile, _) = await StartAsync();
        var host = new FakeHost();
        await using var manager = new ScenarioCaptureService(_store, host, NullLogger<ScenarioCaptureService>.Instance);
        Assert.Throws<InvalidOperationException>(manager.StartRange);
        await manager.BeginGameAsync("game"); await manager.SelectProcessAsync(Environment.ProcessId);
        var source = Source with { ProcessId = (uint)Environment.ProcessId };
        await manager.SaveRulesAsync(new() { Selections = [new(source.HookCode, source.HookName, source.Context, source.Context2, CaptureRole.Body)] });
        await manager.ConfirmProcessAsync();
        manager.StartRange();
        Assert.Throws<InvalidOperationException>(manager.StartRange);
        Assert.Throws<InvalidOperationException>(() => manager.EndRange());
        Assert.NotNull(manager.Snapshot.RangeStart);
        host.Emit(source, "先頭の区間");
        var first = manager.EndRange();
        host.Emit(source, "区間外");
        manager.StartRange(); host.Emit(source, "区間内");
        var second = manager.EndRange();
        host.Emit(source, "終了後");
        await manager.FlushPendingAsync();
        var firstId = await _store.SaveCaptureRangeAsync(profile.Id, "先頭", first.Start, first.End);
        var secondId = await _store.SaveCaptureRangeAsync(profile.Id, "途中", second.Start, second.End);
        Assert.Equal("先頭の区間", Assert.Single(await _store.GetGameCaptureItemsAsync(profile.Id, rangeId: firstId)).Text);
        Assert.Equal("区間内", Assert.Single(await _store.GetGameCaptureItemsAsync(profile.Id, rangeId: secondId)).Text);
        manager.StartRange(); manager.CancelRange(); Assert.Null(manager.Snapshot.RangeStart);
        manager.StartRange(); host.Emit(source, "未完了でも保存する本文");
        await manager.FinishAsync();
        Assert.Null(manager.Snapshot.RangeStart);
        Assert.Equal(2, (await _store.GetCaptureRangesAsync(profile.Id)).Count);
        Assert.Equal(new[] { "先頭の区間", "区間外", "区間内", "終了後", "未完了でも保存する本文" }, (await _store.GetGameCaptureItemsAsync(profile.Id)).Select(x => x.Text));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "接続失敗から再試行できプロセスの選び直しは既存本文と確定済みの紐付けを保つ"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#接続と受信確認" }
    // fault = "失敗後に再接続できない、テスト先を直ちに記憶する、または切替時に既存本文を消す"
    // observable = "再接続後のsnapshot、確定前後の本体パス、再読込した全本文と取得回の終了"
    // observation_boundary = "public-boundary"
    // scope = "capture process retry, confirmation and session separation"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ProcessRetryAndReselectionPreserveConfirmedHistory()
    {
        var (profile, _) = await StartAsync();
        var host = new FakeHost { FailNextConnection = true };
        await using var manager = new ScenarioCaptureService(_store, host, NullLogger<ScenarioCaptureService>.Instance);
        await manager.BeginGameAsync("game");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SelectProcessAsync(Environment.ProcessId));
        Assert.False(manager.Snapshot.Connected);
        Assert.True(manager.Snapshot.NeedsProcess);
        Assert.Null((await _store.GetCaptureProfilesAsync()).Single().BodyExecutablePath);
        await manager.SelectProcessAsync(Environment.ProcessId);
        var source = Source with { ProcessId = (uint)Environment.ProcessId };
        host.Emit(source, "テスト中の文章");
        await manager.SaveRulesAsync(new() { Selections = [new(source.HookCode, source.HookName, source.Context, source.Context2, CaptureRole.Body)] });
        await manager.ConfirmProcessAsync();
        host.Emit(source, "「確定した本文」");
        await manager.FlushPendingAsync();
        var first = manager.Snapshot.SessionId!.Value;
        host.Path = "C:\\different-game.exe";
        await manager.SelectProcessAsync(Environment.ProcessId);
        Assert.True(manager.Snapshot.Preview);
        Assert.Null(manager.Snapshot.SessionId);
        Assert.Equal(Environment.ProcessPath, (await _store.GetCaptureProfilesAsync()).Single().BodyExecutablePath);
        Assert.NotNull((await _store.GetCaptureSessionsAsync(profile.Id)).Single(x => x.Id == first).EndedAt);
        host.Emit(source, "切替後のテスト");
        await manager.ConfirmProcessAsync();
        host.Emit(source, "「切替後の本文」");
        await manager.FinishAsync();
        Assert.Equal(host.Path, (await _store.GetCaptureProfilesAsync()).Single().BodyExecutablePath);
        Assert.Equal(new[] { "「確定した本文」", "「切替後の本文」" }, (await _store.GetGameCaptureItemsAsync(profile.Id, 0)).Select(x => x.Text));
        using var process = Process.GetCurrentProcess();
        Assert.False(process.HasExited);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ゲーム本体の検索はウィンドウ名・プロセス名・PID・パスを部分一致で探せる"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#接続と受信確認" }
    // fault = "プロセス名だけを検索する、または英字の大小で該当候補を落とす"
    // observable = "各検索語と非該当語に対するprocess検索の一致結果"
    // observation_boundary = "component-behavior"
    // scope = "capture process search"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void ProcessSearchFindsWindowNamePidAndPath()
    {
        var process = new CaptureProcess(12345, "story-engine", "C:\\Games\\story.exe", "風待ちの街");
        foreach (var query in new[] { "風待ち", "STORY-ENGINE", "234", "games\\story", "  風待ち  ", "" })
            Assert.True(process.Matches(query));
        Assert.False(process.Matches("another-game"));
    }

    // @test-value v2
    // kind = "compatibility"
    // claim = "固定分類での閲覧とTXTは保存済みの独自分類名と同種別の本文を欠かさない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#原文と分類" }
    // fault = "分類絞り込みが最初のラベルIDだけを読み同種別の独自名データを除外する"
    // observable = "再読込した分類名と本文、種別指定の全件・ページ・TXT、非該当種別の結果"
    // observation_boundary = "public-boundary"
    // scope = "existing capture label references and fixed-kind retrieval"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task FixedKindFilterKeepsExistingCustomClassification()
    {
        var (profile, session) = await StartAsync();
        long label;
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO CaptureLabels(CaptureGameId,Name,Kind) VALUES($game,'心の声',0) RETURNING Id";
            command.Parameters.AddWithValue("$game", profile.Id);
            label = (long)(await command.ExecuteScalarAsync())!;
        }
        await _store.AppendCaptureAsync(session, Receive(1, "「発言」"), new());
        await _store.AppendCaptureAsync(session, Receive(2, "内心") with { LabelId = label }, new());
        await _store.AppendCaptureAsync(session, Receive(3, "地の文"), new() { OutsideIsNarration = true });
        var reopened = new SqliteGameLibraryStore(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
        var dialogue = await reopened.GetGameCaptureItemsAsync(profile.Id, 0, kind: CaptureKind.Dialogue);
        Assert.Equal(new[] { "「発言」", "内心" }, dialogue.Select(x => x.Text));
        Assert.Equal(new[] { "セリフ", "心の声" }, dialogue.Select(x => x.LabelName));
        Assert.Equal("内心", Assert.Single(await reopened.GetGameCaptureItemsAsync(profile.Id, 1, 1, kind: CaptureKind.Dialogue)).Text);
        Assert.Equal("地の文", Assert.Single(await reopened.GetGameCaptureItemsAsync(profile.Id, 0, kind: CaptureKind.Narration)).Text);
        Assert.Contains("[心の声：話者未確定]", ScenarioTextProcessor.FormatTxt(dialogue));
        Assert.Equal(3, (await reopened.GetGameCaptureItemsAsync(profile.Id, 0)).Count);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "取得元ごとの受信例は到着順と識別を保ち直近5件・各1500文字・256候補に収まり、切詰め例は判定せず切断で破棄される"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#接続と受信確認" }
    // fault = "候補更新で並べ替える、取得元間で例を混ぜる、上限なく保持する、切詰め本文を判定する、または切断後もテスト本文を残す"
    // observable = "snapshotの候補順・各sampleの連番と本文と切詰め表示・切断後の候補"
    // observation_boundary = "public-boundary"
    // scope = "capture candidate sample retention"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task CandidateSamplesKeepBoundedRecentCallbacksAndSourceOrder()
    {
        await StartAsync();
        var host = new FakeHost();
        await using var manager = new ScenarioCaptureService(_store, host, NullLogger<ScenarioCaptureService>.Instance);
        await manager.BeginGameAsync("game"); await manager.SelectProcessAsync(Environment.ProcessId);
        var source = Source with { ProcessId = (uint)Environment.ProcessId };
        var other = source with { Address = source.Address + 1 };
        host.Emit(source, "最初"); host.Emit(other, "別の取得元");
        for (var i = 0; i < 6; i++) host.Emit(source, $"受信{i}");
        var snapshot = manager.Snapshot;
        Assert.Equal(new[] { source, other }, snapshot.Candidates.Select(x => x.Source));
        Assert.Equal(new[] { "受信1", "受信2", "受信3", "受信4", "受信5" }, snapshot.Candidates[0].Samples.Select(x => x.Text));
        Assert.Equal(new long[] { 4, 5, 6, 7, 8 }, snapshot.Candidates[0].Samples.Select(x => x.Sequence));
        Assert.Equal("別の取得元", Assert.Single(snapshot.Candidates[1].Samples).Text);
        host.Emit(source, new string('長', 1501));
        var last = manager.Snapshot.Candidates[0].Samples[^1];
        Assert.True(last.Truncated); Assert.Equal(1500, last.Text.Length);
        await manager.SaveRulesAsync(new() { Selections = [new(source.HookCode, source.HookName, source.Context, source.Context2, CaptureRole.Body)] }, source);
        Assert.NotEmpty(manager.Snapshot.PreviewItems);
        Assert.DoesNotContain(manager.Snapshot.PreviewItems, item => item.RawText == last.Text);
        Assert.Equal(snapshot.Candidates[0].Samples[^1].ReceivedAt, manager.Snapshot.Candidates[0].Samples[^2].ReceivedAt);
        for (var i = 0; i < 260; i++) host.Emit(source with { Address = (ulong)(1000 + i) }, "候補");
        Assert.Equal(256, manager.Snapshot.Candidates.Length);
        Assert.All(manager.Snapshot.Candidates, candidate => Assert.InRange(candidate.Samples.Length, 1, 5));
        await manager.FinishAsync();
        Assert.Empty(manager.Snapshot.Candidates);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "テスト結果は現在の用途・分割・固定分類を再計算し、名前登録なしで正式記録と同じ話者・本文・TXTを生成する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#接続と受信確認" }
    // fault = "試験専用の分類や話者処理を使う、ルール変更前の結果を残す、または試験本文を履歴へ保存する"
    // observable = "snapshotの分割・分類・話者・TXTと同じ入力の正式保存結果、試験中のDB本文件数と確定後の原文"
    // observation_boundary = "public-boundary"
    // scope = "in-memory preview processing and formal recording boundary"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task PreviewRecomputesCurrentRulesUsingFormalTextProcessingWithoutSavingSamples()
    {
        var (profile, oracleSession) = await StartAsync();
        var host = new FakeHost();
        await using var manager = new ScenarioCaptureService(_store, host, NullLogger<ScenarioCaptureService>.Instance);
        await manager.BeginGameAsync("game"); await manager.SelectProcessAsync(Environment.ProcessId);
        var source = Source with { ProcessId = (uint)Environment.ProcessId };
        var nameSource = source with { Address = 101, HookName = "name" };
        var rules = new CaptureRules { OutsideIsNarration = true, Selections = [
            new(source.HookCode, source.HookName, source.Context, source.Context2, CaptureRole.Body),
            new(nameSource.HookCode, nameSource.HookName, nameSource.Context, nameSource.Context2, CaptureRole.Name)] };
        await manager.SaveRulesAsync(rules);
        const string raw = "朝「おはよう」余韻";
        host.Emit(source, raw); host.Emit(nameSource, " 美咲 ");
        Assert.Equal(new CaptureKind?[] { CaptureKind.Narration, CaptureKind.Dialogue, CaptureKind.Narration }, manager.Snapshot.PreviewItems.Select(x => x.Kind));
        await Task.Delay(2100);
        var preview = manager.Snapshot.PreviewItems;
        Assert.Equal(raw, string.Concat(preview.Select(x => x.Text)));
        Assert.Equal("美咲", Assert.Single(preview, x => x.Kind == CaptureKind.Dialogue).Speaker);
        Assert.Empty(await _store.GetGameCaptureItemsAsync(profile.Id, 0));
        Assert.Empty((await _store.GetCaptureProfilesAsync()).Single().Rules.Selections);
        foreach (var candidate in manager.Snapshot.Candidates)
            foreach (var sample in candidate.Samples)
                await _store.AppendCaptureAsync(oracleSession, new(sample.Sequence, sample.ReceivedAt, candidate.Source, sample.Text, candidate.Role, candidate.LabelId), rules);
        await _store.ResolveCaptureSpeakersAsync(oracleSession, preview[0].ReceivedAt, DateTimeOffset.UtcNow);
        Assert.Equal(ScenarioTextProcessor.FormatTxt(await _store.GetCaptureItemsAsync(oracleSession)), ScenarioTextProcessor.FormatTxt(preview));
        await manager.SaveRulesAsync(rules with { Selections = [rules.Selections[0] with { Role = CaptureRole.None }, rules.Selections[1]] });
        Assert.Empty(manager.Snapshot.PreviewItems);
        var narration = Assert.Single(await _store.GetCaptureLabelsAsync(profile.Id), x => x.Kind == CaptureKind.Narration);
        await manager.SaveRulesAsync(rules with { Selections = [rules.Selections[0] with { LabelId = narration.Id }, rules.Selections[1]] });
        var fixedPreview = manager.Snapshot.PreviewItems;
        Assert.Equal(new[] { "朝", "「おはよう」", "余韻" }, fixedPreview.Select(x => x.Text));
        Assert.All(fixedPreview, x => { Assert.Equal(CaptureKind.Narration, x.Kind); Assert.Null(x.Speaker); });
        await manager.SaveRulesAsync(rules with { SplitDialogue = false });
        var whole = Assert.Single(manager.Snapshot.PreviewItems);
        Assert.Equal(raw, whole.Text); Assert.Null(whole.Kind);
        await manager.ConfirmProcessAsync();
        Assert.Empty(manager.Snapshot.PreviewItems);
        Assert.All(manager.Snapshot.Candidates, candidate => Assert.Empty(candidate.Samples));
        var fullRaw = new string('文', 1600);
        host.Emit(source, fullRaw);
        await manager.FlushPendingAsync();
        var recorded = Assert.Single(await _store.GetCaptureItemsAsync(manager.Snapshot.SessionId!.Value));
        Assert.Equal(fullRaw, recorded.RawText); Assert.Equal(1, recorded.Sequence);
        await manager.SelectProcessAsync(Environment.ProcessId);
        Assert.Empty(manager.Snapshot.Candidates); Assert.Empty(manager.Snapshot.PreviewItems);
    }

    private sealed class FakeHost : ILunaCaptureHost
    {
        public event Action<CaptureSource, string>? Output;
        public event Action<uint, bool>? ConnectionChanged;
        public int Connections, Detaches;
        public bool FailNextConnection;
        public string Path = Environment.ProcessPath!;
        public Task ConnectAsync(int processId) { Connections++; if (FailNextConnection) { FailNextConnection = false; throw new InvalidOperationException("test failure"); } ConnectionChanged?.Invoke((uint)processId, true); return Task.CompletedTask; }
        public void Detach(int processId) { Detaches++; ConnectionChanged?.Invoke((uint)processId, false); }
        public IReadOnlyList<CaptureProcess> GetProcesses() => [new(Environment.ProcessId, "test-body", Path, "テスト用ゲーム")];
        public CaptureProcess? GetForegroundProcess() => GetProcesses()[0];
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
                DROP TABLE CaptureRanges; DROP TABLE CaptureItems; DROP TABLE CaptureEvents; DROP TABLE CaptureSources;
                DROP TABLE CaptureSessions; DROP TABLE CaptureLabels; DROP TABLE CaptureGames;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var upgraded = new SqliteGameLibraryStore(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
        var profile = await upgraded.GetOrCreateCaptureProfileAsync("game");
        Assert.Equal(game, await upgraded.FindByIdAsync("game"));
        Assert.Equal(links, await upgraded.GetByGameIdAsync("game"));
        Assert.Equal(history, (await upgraded.GetLatestByRepositoryIdsAsync(["repo"], 10))["repo"]);
        Assert.Equal(new[] { CaptureKind.Dialogue, CaptureKind.Narration }, (await upgraded.GetCaptureLabelsAsync(profile.Id)).Select(x => x.Kind));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
