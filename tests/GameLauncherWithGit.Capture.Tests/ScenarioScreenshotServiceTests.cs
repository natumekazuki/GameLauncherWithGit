using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioScreenshotServiceTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(), "screenshot-service-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteGameLibraryStore _store;
    private readonly FakeHost _host = new();
    private readonly FakePlatform _platform = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeNotifications _notifications = new();
    private readonly FakeClock _clock = new();
    private readonly ScenarioCaptureService _capture;
    private readonly ScenarioScreenshotService _screenshots;
    private static readonly CaptureSource Body = new((uint)Environment.ProcessId, 100, 200, 0, "body-hook", "body");
    private static readonly CaptureSource Name = Body with { Address = 101, HookCode = "name-hook", HookName = "name" };

    public ScenarioScreenshotServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
        _capture = new(_store, _host, NullLogger<ScenarioCaptureService>.Instance);
        _screenshots = new(_capture, _store, _platform, _settings, NullLogger<ScenarioScreenshotService>.Instance, _notifications, _clock);
    }

    private async Task BeginPreviewAsync(string game = "game")
    {
        await _store.UpsertAsync(new(game, game, "game.exe", null, null, null, GameCardStatus.Unknown));
        Assert.True(await _capture.BeginGameAsync(game));
        await _capture.SelectProcessAsync(Environment.ProcessId);
        await _capture.SaveRulesAsync(new() { Selections = [
            new(Body.HookCode, Body.HookName, Body.Context, Body.Context2, CaptureRole.Body),
            new(Name.HookCode, Name.HookName, Name.Context, Name.Context2, CaptureRole.Name)] });
    }

    private PlayRecordingOperations Play => new(_capture, _store, _screenshots, _store);

    // @test-value v2
    // kind = "invariant"
    // claim = "PCで始めWebで終了した同じ区間の同時保存は、固定された本文の境界で一度だけ成功する"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / play-recording-web-sharing-proposal; README.md: シナリオ抽出" }
    // fault = "端末ごとに区間状態を分ける、保存時の最新本文で終了位置を置き換える、または同じ確定区間を二重作成する"
    // observable = "PCとWebが取得する区間選択、競合時の成功数、SQLiteの保存区間の件数・境界・タグ"
    // observation_boundary = "public-boundary"
    // scope = "shared live range from desktop start through web freeze and concurrent save"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task DesktopAndWebSaveTheSameFrozenLiveRangeOnlyOnce()
    {
        await BeginPreviewAsync(); await _capture.ConfirmProcessAsync();
        var play = Play;
        var profile = _capture.Snapshot.Profile!.Id;
        var start = PlayRecordingOperations.DesktopTarget(_capture.Snapshot, profile);
        await play.StartDesktopRangeAsync(start);
        var live = await play.GetStateAsync("game");
        Assert.NotNull(live.RangeStart);
        Assert.False(live.CanEndRange);
        _host.Emit(Body, "この本文で終了");
        var frozen = await play.EndRangeAsync("game", live.Target);
        var ready = await play.GetStateAsync("game");
        Assert.Same(frozen, ready.PendingRange);
        Assert.Same(frozen, await play.GetDesktopPendingRangeAsync(ready.Target));
        _host.Emit(Body, "保存操作までに届いた次の本文");

        static async Task<bool> Saved(Task<long> call)
        {
            try { return await call > 0; }
            catch (InvalidOperationException) { return false; }
        }
        var successes = await Task.WhenAll(
            Saved(play.SaveDesktopRangeAsync(ready.Target, frozen, "PC", ["PC"])),
            Saved(play.SaveRangeAsync("game", ready.Target, frozen, "Web", ["Web"])));
        Assert.Single(successes, saved => saved);
        var after = await play.GetStateAsync("game");
        Assert.Null(after.PendingRange);
        var range = Assert.Single(await play.GetRangesAsync("game", after.Target));
        Assert.Equal(frozen.Start, range.Start);
        Assert.Equal(frozen.End, range.End);
        Assert.Equal(1, range.End.Sequence);
        Assert.Equal(range.Name, Assert.Single(range.Tags));
        Assert.Single(await _store.GetGameCaptureItemsAsync(profile, rangeId: range.Id));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "取得を終了して別ゲームに切り替えた場合と同じゲームの新しい取得回で、以前のWeb表示・区間を再利用できない"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / play-web-product-coordination; play-web-recording-operations-work" }
    // fault = "gameId一致だけで古いボタンを受け入れる、他ゲーム取得中に旧選択を書き込む、または取消済み区間を保存する"
    // observable = "各操作の拒否、現在の取得回IDと取得状態、旧本文の可読性、両ゲームの保存区間件数"
    // observation_boundary = "public-boundary"
    // scope = "capture lifecycle, re-entry for the same configured game, and range cancellation"
    // lifecycle = "permanent"
    // risk_tags = ["authorization"]
    // @end-test-value
    [Fact]
    public async Task OldTargetsCannotActAfterCancellationOrSameGameReentry()
    {
        await BeginPreviewAsync(); await _capture.ConfirmProcessAsync();
        var play = Play;
        var first = await play.GetStateAsync("game");
        await play.StartRangeAsync("game", first.Target);
        _host.Emit(Body, "初回本文");
        var collecting = await play.GetStateAsync("game");
        var frozen = await play.EndRangeAsync("game", collecting.Target);
        var draft = await play.GetStateAsync("game");
        await play.CancelDesktopRangeAsync(draft.Target);
        var cancelled = await play.GetStateAsync("game");
        Assert.Null(cancelled.PendingRange);
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.SaveRangeAsync("game", cancelled.Target, frozen, "取消済み", []));
        await play.FinishAsync("game", cancelled.Target);
        await BeginPreviewAsync("other"); await _capture.ConfirmProcessAsync();
        var blocked = await play.GetStateAsync("game");
        Assert.False(blocked.CanStart);
        Assert.True(blocked.BlockedByOtherGame);
        Assert.Null(blocked.Target.SessionId);
        Assert.Equal("game", blocked.Title);
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.GetItemsAsync("game", blocked.Target));
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.FinishAsync("game", draft.Target));
        var other = await play.GetStateAsync("other");
        Assert.True(other.Recording);
        await play.FinishAsync("other", other.Target);

        var beforeRestart = await play.GetStateAsync("game");
        Assert.True(beforeRestart.CanStart);
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.StartAsync("game", beforeRestart.Target));
        var otherProfile = (await _store.FindCaptureProfileAsync("other"))!;
        await _store.SaveCaptureProfileAsync(otherProfile with { BodyExecutablePath = "other.exe" });
        await play.StartAsync("game", beforeRestart.Target);
        var restarted = await play.GetStateAsync("game");
        Assert.True(restarted.Recording);
        Assert.NotEqual(first.Target.SessionId, restarted.Target.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.PauseAsync("game", first.Target));
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.StartRangeAsync("game", cancelled.Target));
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.SaveRangeAsync("game", restarted.Target, frozen, "旧取得回", []));
        Assert.Empty(await play.GetRangesAsync("game", restarted.Target));
        Assert.Empty(await _store.GetCaptureRangesAsync(other.Target.ProfileId));
        Assert.Equal("初回本文", Assert.Single((await play.GetItemsAsync("game", restarted.Target)).Items).Text);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "Web撮影はゲーム窓専用経路を使い、許可ゲームの対象本文に添付したPNGだけを返す"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / play-web-product-coordination; play-web-recording-operations-work" }
    // fault = "PCで手動指定した別PID窓やデスクトップ画素を取得する、または他ゲームや異なる本文の画像IDを読み出す"
    // observable = "platformの使用経路と対象窓、後続本文が届いた後に返る固定済み本文と画像ID、PNGバイト、別本文・別ゲームの読取拒否"
    // observation_boundary = "public-boundary"
    // scope = "game-only screenshot source and reading the exact game, event and image association"
    // lifecycle = "permanent"
    // risk_tags = ["authorization", "privacy"]
    // @end-test-value
    [Fact]
    public async Task GameScreenshotIgnoresDesktopWindowOverrideAndCannotBeReadFromAnotherEvent()
    {
        await BeginPreviewAsync(); await _capture.ConfirmProcessAsync();
        var play = Play;
        var state = await play.GetStateAsync("game");
        _screenshots.SelectWindow(_platform.Windows[1]);
        _host.Emit(Body, "撮影対象");
        var key = _capture.FreezeScreenshotTarget().Target.Event;
        var (entered, release) = _platform.HoldNextGameFrame();
        var taking = play.TakeScreenshotAsync("game", state.Target);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _host.Emit(Body, "画像なし本文");
        }
        finally { release.TrySetResult(FakePlatform.Frame); }
        var result = await taking;
        Assert.Equal(key, result.Event);
        Assert.Empty(_platform.Captured);
        Assert.Equal(_platform.Windows[0], Assert.Single(_platform.GameWindowsCaptured));
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.TakeScreenshotAsync("game", state.Target));
        Assert.Single(_platform.GameWindowsCaptured);
        state = await play.GetStateAsync("game");
        var image = Assert.Single(await play.GetScreenshotsAsync("game", state.Target, key));
        Assert.Equal(image.ImageId, result.ImageId);
        Assert.Equal(FakePlatform.Frame.PngBytes, await play.ReadScreenshotAsync("game", state.Target, key, image.ImageId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.ReadScreenshotAsync("game", state.Target,
            key with { Sequence = key.Sequence + 1 }, image.ImageId));
        await play.FinishAsync("game", state.Target);
        await BeginPreviewAsync("other"); await _capture.ConfirmProcessAsync();
        var other = await play.GetStateAsync("other");
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.ReadScreenshotAsync("other", other.Target, key, image.ImageId));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "Webのゲーム窓描画が非対応なら失敗として通知し、PCの共有状態も保存中から失敗へ移り、画像やデスクトップ描画への代替を残さず終了できる"
    // oracle = { type = "contract", ref = "README.md: プレイ中の記録をスマホ・タブレットで操作する（Windows）; WithMate Session Graph play-recording-web / play-web-product-coordination" }
    // fault = "窓描画拒否後も共有状態を保存中と表示する、PC撮影へ切り替える、失敗画像を保存する、または終了時に処理済みの撮影失敗を再送出する"
    // observable = "Web失敗と共有の状態文言、保存中フラグと保存件数、専用窓描画のみ一回、関連画像なし、サービス終了は成功"
    // observation_boundary = "public-boundary"
    // scope = "game-only capture failure and shared screenshot service shutdown"
    // lifecycle = "permanent"
    // risk_tags = ["privacy"]
    // @end-test-value
    [Fact]
    public async Task UnsupportedGameFrameDoesNotFallBackOrPreventShutdown()
    {
        await BeginPreviewAsync(); await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "撮影対象");
        var key = _capture.FreezeScreenshotTarget().Target.Event;
        var play = Play;
        var state = await play.GetStateAsync("game");
        var (entered, release) = _platform.HoldNextGameFrame();
        var request = play.TakeScreenshotAsync("game", state.Target);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetException(new InvalidOperationException("Game window rendering is unavailable.")); }
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => request);

        Assert.False(_screenshots.Snapshot.Busy);
        Assert.Equal(failure.Message, _screenshots.Snapshot.Status);
        Assert.Equal(0, _screenshots.Snapshot.SavedCount);

        Assert.Equal(_platform.Windows[0], Assert.Single(_platform.GameWindowsCaptured));
        Assert.Empty(_platform.Captured);
        Assert.Empty(await play.GetScreenshotsAsync("game", (await play.GetStateAsync("game")).Target, key));
        await _screenshots.DisposeAsync();
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "PCによる同ゲームの取得元切替に割り込んだWeb保存は、旧区間を新しい取得回や旧履歴へ書かない"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / play-web-recording-operations-work" }
    // fault = "取得元の切替と区間保存が異なる排他境界を使い、すでに切替中の旧選択から保存できる"
    // observable = "擬似接続中のWeb保存待ち、切替後の保存拒否、取得回IDとSQLite区間件数"
    // observation_boundary = "public-boundary"
    // scope = "independently scheduled PC capture-source switch and web save"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task WebSaveWaitsForDesktopSourceSwitchAndRejectsTheOldSelection()
    {
        await BeginPreviewAsync(); await _capture.ConfirmProcessAsync();
        var play = Play;
        var first = await play.GetStateAsync("game");
        await play.StartRangeAsync("game", first.Target);
        _host.Emit(Body, "切替前の本文");
        var frozen = await play.EndRangeAsync("game", (await play.GetStateAsync("game")).Target);
        var ready = await play.GetStateAsync("game");
        var (entered, release) = _host.HoldNextConnection();
        var switching = _capture.SelectProcessAsync(Environment.ProcessId, preview: false);
        Task<long> save;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            save = play.SaveRangeAsync("game", ready.Target, frozen, "旧区間", []);
            Assert.False(save.IsCompleted);
        }
        finally { release.TrySetResult(); await switching; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => save);
        var after = await play.GetStateAsync("game");
        Assert.NotEqual(first.Target.SessionId, after.Target.SessionId);
        Assert.Null(after.PendingRange);
        Assert.Empty(await play.GetRangesAsync("game", after.Target));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "本文ページ境界は新着でずれず、話者・分類の同時訂正は表示時と同じ本文に両方反映されるか両方とも拒否される"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / play-web-product-coordination; README.md: シナリオ抽出" }
    // fault = "新着でページがずれる、旧表示が訂正を上書きする、不正な分類で話者だけ保存する、または正常な同時訂正で一方が消える"
    // observable = "本文ページの列、旧表示拒否、不正な分類の後の不変行、同時訂正を再読取した話者と分類"
    // observation_boundary = "public-boundary"
    // scope = "live keyset pagination and compare-before-write text corrections"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task OlderTextPageStaysStableWhileReceivingAndCorrectionRejectsAnOldRow()
    {
        await BeginPreviewAsync(); await _capture.ConfirmProcessAsync();
        var play = Play;
        var state = await play.GetStateAsync("game");
        foreach (var text in new[] { "一", "ニ", "三", "四" }) _host.Emit(Body, text);
        await _capture.FlushPendingAsync();
        var first = await play.GetItemsAsync("game", state.Target, limit: 2);
        Assert.Equal(new[] { "三", "四" }, first.Items.Select(x => x.Text));
        _host.Emit(Body, "五"); await _capture.FlushPendingAsync();
        var older = await play.GetItemsAsync("game", state.Target, limit: 2, before: first.NextBefore);
        Assert.Equal(new[] { "一", "ニ" }, older.Items.Select(x => x.Text));
        Assert.False(older.HasMore);
        var edit = older.Items[0];
        await play.SetSpeakerAsync("game", state.Target, edit, "真夜");
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.SetSpeakerAsync("game", state.Target, edit, "古い入力"));
        var current = (await play.GetItemsAsync("game", state.Target, limit: 2, before: first.NextBefore)).Items[0];
        Assert.Equal("真夜", current.Speaker);
        var dialogue = Assert.Single(await play.GetLabelsAsync("game", state.Target), x => x.Kind == CaptureKind.Dialogue);
        await Assert.ThrowsAsync<ArgumentException>(() => play.CorrectTextAsync("game", state.Target, current,
            changeLabel: true, labelId: long.MaxValue, changeSpeaker: true, speaker: "反映されない話者"));
        Assert.Equal(current, await play.GetItemAtAsync("game", state.Target, current.StartPosition));
        await play.CorrectTextAsync("game", state.Target, current,
            changeLabel: true, labelId: dialogue.Id, changeSpeaker: true, speaker: "灯里");
        var corrected = (await play.GetItemsAsync("game", state.Target, limit: 2, before: first.NextBefore)).Items[0];
        Assert.Equal(("灯里", CaptureKind.Dialogue), (corrected.Speaker, corrected.Kind));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "区間で本文を絞り、観測出現の位置を開いたり切ったりしても、手動場面の有向接続は観測経路に混ざらない"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / play-web-screenshot-result-and-flow-navigation; README.md: シナリオ抽出（フローチャートと場面フロー）" }
    // fault = "区間の所有を無視する、出現内の指定位置を本文先頭へずらす、古い場面でPCの編集を上書きする、または手動辺を観測辺に混ぜる"
    // observable = "区間本文と範囲外拒否、観測cut位置、PC編集後のWeb更新拒否、現在の手動辺と観測辺"
    // observation_boundary = "public-boundary"
    // scope = "navigation, observed occurrence cut, and separately authored scene connection"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task RangeAndOccurrenceNavigationKeepAuthoredConnectionsOutOfObservedFlow()
    {
        await BeginPreviewAsync(); await _capture.ConfirmProcessAsync();
        var play = Play;
        var state = await play.GetStateAsync("game");
        _host.Emit(Body, "はじめ"); _host.Emit(Body, "後半から分岐");
        await _capture.FlushPendingAsync();
        var texts = (await play.GetItemsAsync("game", state.Target)).Items;
        var second = texts[1];
        var selection = await play.SelectRangeAsync("game", state.Target, second, second);
        var range = await play.SaveRangeAsync("game", state.Target, selection, "分岐", []);
        state = await play.GetStateAsync("game");
        Assert.Equal(second, Assert.Single((await play.GetItemsAsync("game", state.Target, rangeId: range)).Items));
        await _store.UpsertAsync(new("other", "other", "other.exe", null, null, null, GameCardStatus.Unknown));
        var theirProfile = await _store.GetOrCreateCaptureProfileAsync("other");
        var theirSession = await _store.BeginCaptureSessionAsync(theirProfile.Id);
        await _store.AppendCaptureAsync(theirSession, new(1, DateTimeOffset.UtcNow, Body, "他の本文", CaptureRole.Body), new());
        var theirRange = await _store.SaveCaptureRangeAsync(theirProfile.Id, "他の区間", new(theirSession, 1, 0), new(theirSession, 1, 4));
        await Assert.ThrowsAsync<ArgumentException>(() => play.GetItemsAsync("game", state.Target, rangeId: theirRange));
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.GetItemAtAsync("game", state.Target, new(theirSession, 1, 0)));

        var middle = second.StartPosition with { Offset = second.Start + 2 };
        Assert.Equal(second, await play.GetItemAtAsync("game", state.Target, middle));
        await play.CutPathAsync("game", state.Target, middle);
        state = await play.GetStateAsync("game");
        Assert.Contains(middle, (await _store.GetScenarioFlowOverridesAsync(state.Target.ProfileId)).Cuts);
        var observed = (await play.GetObservedFlowAsync("game", state.Target)).Graph;
        var firstScene = await play.CreateSceneAsync("game", state.Target, new("開始", null, null, 0, 0, [], []));
        state = await play.GetStateAsync("game");
        var lastScene = await play.CreateSceneAsync("game", state.Target, new("分岐", null, null, 0, 0, [range], []));
        state = await play.GetStateAsync("game");
        var flow = await play.GetSceneFlowAsync("game", state.Target);
        var from = Assert.Single(flow.Scenes, x => x.Id == firstScene);
        var to = Assert.Single(flow.Scenes, x => x.Id == lastScene);
        await _store.UpdateScenarioSceneAsync(state.Target.ProfileId, firstScene, from.Definition with { Title = "PCの開始" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => play.UpdateSceneAsync("game", state.Target, from, from.Definition with { Title = "旧表示" }));
        flow = await play.GetSceneFlowAsync("game", state.Target);
        from = Assert.Single(flow.Scenes, x => x.Id == firstScene);
        await play.SetSceneConnectionAsync("game", state.Target, from, to, connected: true);
        state = await play.GetStateAsync("game");
        flow = await play.GetSceneFlowAsync("game", state.Target);
        Assert.Equal("PCの開始", from.Definition.Title);
        Assert.Equal(new ScenarioSceneConnection(firstScene, lastScene), Assert.Single(flow.Connections));
        Assert.Equal(range, Assert.Single(to.Definition.RangeIds));
        var after = (await play.GetObservedFlowAsync("game", state.Target)).Graph;
        Assert.Equal(observed.Edges.Select(x => (x.FromBlockId, x.ToBlockId)), after.Edges.Select(x => (x.FromBlockId, x.ToBlockId)));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "テスト受信・本文未受信・名前のみ・空本文・終了後は撮影せず、空白だけの本文は保存しても撮影対象を進めない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "テスト受信や名前や空本文を対象として採用する、空白本文で直前の有効本文を置き換える、または終了した取得回へ撮影する"
    // observable = "FreezeScreenshotTargetの例外と対象、platform撮影件数、保存件数とイベント別添付、保存された空白原文"
    // observation_boundary = "public-boundary"
    // scope = "screenshot eligibility across preview, recording and finish"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task CaptureRequiresANonemptyBodyInTheCurrentFormalSession()
    {
        await BeginPreviewAsync();
        _host.Emit(Body, "試験本文");
        Assert.Throws<InvalidOperationException>(() => _capture.FreezeScreenshotTarget());
        await _screenshots.CaptureAsync();
        await _capture.ConfirmProcessAsync();
        Assert.Throws<InvalidOperationException>(() => _capture.FreezeScreenshotTarget());
        await _screenshots.CaptureAsync();
        _host.Emit(Name, "美咲");
        _host.Emit(Body, "");
        Assert.Throws<InvalidOperationException>(() => _capture.FreezeScreenshotTarget());
        await _screenshots.CaptureAsync();
        Assert.Empty(_platform.Captured);
        Assert.Equal(0, _screenshots.Snapshot.SavedCount);
        _host.Emit(Body, "正式本文");
        var target = _capture.FreezeScreenshotTarget().Target;
        Assert.Equal(3, target.Event.Sequence);
        const string whitespace = " \t\r\n　";
        _host.Emit(Body, whitespace);
        Assert.Equal(target, _capture.FreezeScreenshotTarget().Target);
        await _screenshots.CaptureAsync();
        Assert.Single(await _store.GetEventScreenshotsAsync(target.CaptureGameId, target.Event));
        Assert.Empty(await _store.GetEventScreenshotsAsync(target.CaptureGameId, new(target.Event.SessionId, 4)));
        Assert.Contains(await _store.GetCaptureItemsAsync(target.Event.SessionId), item => item.Sequence == 4 && item.RawText == whitespace);
        await _capture.FinishAsync();
        Assert.Throws<InvalidOperationException>(() => _capture.FreezeScreenshotTarget());
        await _screenshots.CaptureAsync();
        Assert.Single(_platform.Captured);
        Assert.Equal(1, _screenshots.Snapshot.SavedCount);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "押下時の最後の受付済み非空本文を固定し、名前・破棄本文・別PID窓・後続本文・取得回切替は関連先を変更せず、処理中の再押下を待ち行列へ入れない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "encode完了時の最新本文へ付け替える、撮影窓PIDで本文を付け替える、または重複要求を後で撮影する"
    // observable = "元イベントと後続イベントの保存画像、platformの撮影窓と件数、保存件数"
    // observation_boundary = "public-boundary"
    // scope = "frozen screenshot target and single-flight capture with real SQLite writer"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task PendingFrameKeepsTheOriginalBodyDespiteNewEventsAndANewGame()
    {
        await BeginPreviewAsync();
        await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "一つ前");
        _host.Emit(Body, "固定する本文");
        _host.Emit(Name, "美咲");
        _host.Emit(Body, "");
        _host.Emit(Body, new string('長', 1024 * 1024 + 1));
        var frozen = _capture.FreezeScreenshotTarget();
        Assert.Equal(new CaptureEventKey(_capture.Snapshot.SessionId!.Value, 2), frozen.Target.Event);
        Assert.Equal(Environment.ProcessId, frozen.ProcessId);
        var manualWindow = _platform.Windows[1];
        Assert.NotEqual(Environment.ProcessId, manualWindow.ProcessId);
        _screenshots.SelectWindow(manualWindow);
        var frame = _platform.HoldNextFrame();
        var first = _screenshots.CaptureAsync();
        Assert.False(first.IsCompleted);
        Assert.True(_screenshots.Snapshot.Busy);
        _host.Emit(Body, "後続本文");
        var later = _capture.FreezeScreenshotTarget().Target;
        Assert.Equal(6, later.Event.Sequence);
        await _screenshots.CaptureAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(_platform.Captured);
        await _capture.FinishAsync();
        await BeginPreviewAsync("other");
        await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "別ゲーム本文");
        var other = _capture.FreezeScreenshotTarget().Target;
        Assert.NotEqual(frozen.Target.CaptureGameId, other.CaptureGameId);
        frame.SetResult(FakePlatform.Frame);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(manualWindow, Assert.Single(_platform.Captured));
        Assert.Single(await _store.GetEventScreenshotsAsync(frozen.Target.CaptureGameId, frozen.Target.Event));
        Assert.Empty(await _store.GetEventScreenshotsAsync(later.CaptureGameId, later.Event));
        Assert.Empty(await _store.GetEventScreenshotsAsync(other.CaptureGameId, other.Event));
        Assert.Equal(1, _screenshots.Snapshot.SavedCount);
        Assert.False(_screenshots.Snapshot.Busy);
        Assert.False(_screenshots.Snapshot.ManualWindow);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ページを作らなくても正式記録中だけホットキーを登録し、そのcallbackで撮影し、終了後に解除する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "画面の寿命に登録を依存させる、テスト受信中に登録する、または終了後もキーを占有する"
    // observable = "platformの登録キーとcallback、正式記録中の保存画像、終了後の登録状態"
    // observation_boundary = "public-boundary"
    // scope = "application-lifetime hotkey registration and actual callback capture"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task HotkeyWorksWithoutAPageAndUnregistersWhenRecordingEnds()
    {
        await BeginPreviewAsync();
        Assert.Null(_platform.Callback);
        Assert.Empty(_platform.RegisteredKeys);
        await _capture.ConfirmProcessAsync();
        Assert.True(_screenshots.Snapshot.Registered);
        Assert.Equal(new ScreenshotHotkey(5, 0x53), Assert.Single(_platform.RegisteredKeys));
        _host.Emit(Body, "ホットキー対象");
        var target = _capture.FreezeScreenshotTarget().Target;
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() { if (_screenshots.Snapshot.SavedCount == 1) saved.TrySetResult(); }
        _screenshots.Changed += OnChanged;
        try
        {
            Assert.NotNull(_platform.Callback);
            _platform.Callback();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { _screenshots.Changed -= OnChanged; }
        Assert.Single(await _store.GetEventScreenshotsAsync(target.CaptureGameId, target.Event));
        await _capture.FinishAsync();
        Assert.Null(_platform.Callback);
        Assert.False(_screenshots.Snapshot.Registered);
        Assert.Single(_platform.RegisteredKeys);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "撮影処理中に関連先原文がなくなった場合は画像も成功件数も残さない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "原文の存在を確認せず画像を保存する、または保存例外を成功として通知する"
    // observable = "サービス保存件数と失敗状態、削除済み原文の添付読取拒否、PNGファイルの件数"
    // observation_boundary = "public-boundary"
    // scope = "screenshot service propagation of missing persistent body failure"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task MissingPersistentBodyDoesNotLeaveAnImageOrReportSuccess()
    {
        await BeginPreviewAsync();
        await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "消去する本文");
        var target = _capture.FreezeScreenshotTarget().Target;
        var frame = _platform.HoldNextFrame();
        var pending = _screenshots.CaptureAsync();
        await _capture.FlushPendingAsync();
        await _capture.FinishAsync();
        await _store.DeleteCaptureEventsAsync(target.CaptureGameId, [target.Event]);
        frame.SetResult(FakePlatform.Frame);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, _screenshots.Snapshot.SavedCount);
        Assert.False(_screenshots.Snapshot.Busy);
        Assert.NotEqual("スクリーンショットを保存しました", _screenshots.Snapshot.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetEventScreenshotsAsync(target.CaptureGameId, target.Event));
        Assert.Empty(Directory.GetFiles(_directory, "*.png", SearchOption.AllDirectories));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "キー変更の登録または設定保存に失敗したら保存値と旧有効キーを維持し旧callbackで撮影できる"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/23" }
    // fault = "変更失敗後に旧登録を復旧しない、または保存されなかった新キーを有効と表示する"
    // observable = "設定値、有効なキーとcallback、registered状態、実ストアへの保存件数"
    // observation_boundary = "public-boundary"
    // scope = "hotkey replacement recovery"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedHotkeyChangeRestoresTheWorkingKey(bool failSettings)
    {
        await BeginPreviewAsync();
        await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "旧キーで保存する本文");
        var original = _screenshots.Snapshot.Hotkey;
        var replacement = new ScreenshotHotkey(5, 0x54);
        if (failSettings) _settings.FailNextSave = true;
        else _platform.FailedKeys.Add(replacement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _screenshots.ConfigureHotkeyAsync(replacement));
        Assert.Equal(original, _screenshots.Snapshot.Hotkey);
        Assert.Equal(original, _platform.ActiveKey);
        Assert.True(_screenshots.Snapshot.Registered);
        Assert.Equal(original.Modifiers, _settings.Get().ScreenshotModifiers);
        Assert.Equal(original.VirtualKey, _settings.Get().ScreenshotVirtualKey);
        Assert.Contains("以前の Alt+Shift+S", _screenshots.Snapshot.LastFailure!.Message);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() { if (_screenshots.Snapshot.SavedCount == 1) saved.TrySetResult(); }
        _screenshots.Changed += OnChanged;
        try
        {
            _platform.Callback!();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { _screenshots.Changed -= OnChanged; }
        var target = _capture.FreezeScreenshotTarget().Target;
        Assert.Single(await _store.GetEventScreenshotsAsync(target.CaptureGameId, target.Event));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "旧キーの復旧も失敗したら未登録と対処を示し再設定成功後は新キーだけを有効にする"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/23" }
    // fault = "復旧失敗を有効な旧登録と表示する、または再設定成功後も旧キーを保持する"
    // observable = "有効キー、設定値、RegisteredとLastFailureの回復案内"
    // observation_boundary = "public-boundary"
    // scope = "unregistered hotkey recovery and successful replacement"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task FailedRestorationIsExplicitAndAReplacementCanRecover()
    {
        await BeginPreviewAsync();
        await _capture.ConfirmProcessAsync();
        var old = _screenshots.Snapshot.Hotkey;
        var replacement = new ScreenshotHotkey(5, 0x54);
        _platform.FailedKeys.UnionWith([old, replacement]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _screenshots.ConfigureHotkeyAsync(replacement));
        Assert.False(_screenshots.Snapshot.Registered);
        Assert.Null(_platform.ActiveKey);
        Assert.Contains("未登録", _screenshots.Snapshot.LastFailure!.Message);
        Assert.Contains("保存し直して", _screenshots.Snapshot.LastFailure.Message);
        _platform.FailedKeys.Clear();
        await _screenshots.ConfigureHotkeyAsync(replacement);
        Assert.Equal(replacement, _platform.ActiveKey);
        Assert.Equal(replacement, _screenshots.Snapshot.Hotkey);
        Assert.True(_screenshots.Snapshot.Registered);
        Assert.Equal(replacement.VirtualKey, _settings.Get().ScreenshotVirtualKey);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ページなしでも失敗を外部通知し連打を抑制し停止後にも直近失敗と対処を保持する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/24" }
    // fault = "状態欄にしか失敗を残さない、連打をすべて通知する、または停止時に失敗を失う"
    // observable = "通知messageと短音回数、LastFailure、記録継続状態、停止後の保存成功数"
    // observation_boundary = "public-boundary"
    // scope = "application-lifetime screenshot failure feedback"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task FailureFeedbackIsThrottledAndRetainedAfterRecordingStops()
    {
        await BeginPreviewAsync();
        await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "撮影本文");
        _platform.Windows = [];
        await _screenshots.CaptureAsync();
        await _screenshots.CaptureAsync();
        Assert.Single(_notifications.Sent);
        Assert.Equal(1, _platform.Signals);
        Assert.Contains("ウィンドウ", _notifications.Sent[0]);
        Assert.NotNull(_screenshots.Snapshot.LastFailure);
        Assert.True(_capture.Snapshot.Recording);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _screenshots.CaptureAsync();
        Assert.Equal(2, _notifications.Sent.Count);
        Assert.Equal(2, _platform.Signals);
        var lastFailure = _screenshots.Snapshot.LastFailure;
        await _capture.FinishAsync();
        Assert.Equal(lastFailure, _screenshots.Snapshot.LastFailure);
        Assert.Equal(0, _screenshots.Snapshot.SavedCount);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "通常終了は新規撮影を止め受付済み撮影の本文とPNGと添付保存が済むまで待つ"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/27" }
    // fault = "Dispose完了後に撮影タスクだけが残る、または終了開始後に追加撮影する"
    // observable = "終了Taskの未完了と完了、撮影回数、本文添付とPNG存在、保存成功数"
    // observation_boundary = "public-boundary"
    // scope = "normal shutdown drains accepted screenshot capture"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ShutdownWaitsForAcceptedCaptureBeforeFinishing()
    {
        await BeginPreviewAsync();
        await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "終了前の本文");
        var target = _capture.FreezeScreenshotTarget().Target;
        var frame = _platform.HoldNextFrame();
        var pending = _screenshots.CaptureAsync();
        var closing = _screenshots.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        Assert.Null(_platform.Callback);
        await _screenshots.CaptureAsync();
        Assert.Single(_platform.Captured);
        frame.SetResult(FakePlatform.Frame);
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pending.IsCompletedSuccessfully);
        await _capture.FinishAsync();
        var image = Assert.Single(await _store.GetEventScreenshotsAsync(target.CaptureGameId, target.Event));
        Assert.True(File.Exists(image.FilePath));
        Assert.Equal(1, _screenshots.Snapshot.SavedCount);
        Assert.False(_screenshots.Snapshot.Busy);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "終了猶予を超えた撮影は中止完了まで管理し未保存を成功とせず画像と添付を残さない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/27" }
    // fault = "中止要求を撮影へ伝えない、終了Taskだけを打ち切る、または未保存を成功にする"
    // observable = "終了Taskと撮影Taskの完了、保存件数、失敗案内、PNGと添付の不在"
    // observation_boundary = "public-boundary"
    // scope = "cooperative shutdown cancellation of delayed capture"
    // lifecycle = "permanent"
    // impact = "終了直後の画像消失を成功扱いせず処理を管理し続ける必要がある"
    // distinction = "通常終了の成功testは中止経路を通らず、型やbuildではTaskとDBとファイルの結果を確認できない"
    // @end-test-value
    [Fact]
    public async Task ShutdownCancelsAnOverdueCaptureWithoutReportingSuccess()
    {
        await BeginPreviewAsync();
        await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "中止される撮影の本文");
        var target = _capture.FreezeScreenshotTarget().Target;
        _platform.HoldNextFrame();
        var pending = _screenshots.CaptureAsync();
        await _screenshots.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(pending.IsCompletedSuccessfully);
        Assert.False(_screenshots.Snapshot.Busy);
        Assert.Equal(0, _screenshots.Snapshot.SavedCount);
        Assert.Contains("中止", _screenshots.Snapshot.LastFailure!.Message);
        await _capture.FinishAsync();
        Assert.Empty(await _store.GetEventScreenshotsAsync(target.CaptureGameId, target.Event));
        Assert.Empty(Directory.GetFiles(_directory, "*.png", SearchOption.AllDirectories));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "本文writerがDB待機中の終了でも本文flushと受付済み画像の保存を終えてから終了する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/27" }
    // fault = "本文flush前に終了を返す、または未確定本文に対する画像保存を破棄する"
    // observable = "終了Taskの未完了、DB解放後の本文と画像添付、PNG存在"
    // observation_boundary = "public-boundary"
    // scope = "shutdown while the real SQLite body writer is waiting"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ShutdownWaitsWhileTheBodyWriterIsBlocked()
    {
        await BeginPreviewAsync();
        await _capture.ConfirmProcessAsync();
        await using var blocker = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}");
        await blocker.OpenAsync();
        await using var transaction = blocker.BeginTransaction();
        _host.Emit(Body, "DB解放後に保存する本文");
        var target = _capture.FreezeScreenshotTarget().Target;
        var pending = _screenshots.CaptureAsync();
        var closing = _screenshots.DisposeAsync().AsTask();
        Assert.False(pending.IsCompleted);
        Assert.False(closing.IsCompleted);
        await transaction.CommitAsync();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        await _capture.FinishAsync();
        var image = Assert.Single(await _store.GetEventScreenshotsAsync(target.CaptureGameId, target.Event));
        Assert.True(File.Exists(image.FilePath));
        Assert.Single(await _store.GetGameCaptureItemsAsync(target.CaptureGameId));
        Assert.Equal(1, _screenshots.Snapshot.SavedCount);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "画像保存の受付後に保存層で待機していても終了は保存と関連確定まで待つ"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/27" }
    // fault = "platform取得完了だけで終了を返し保存待ちTaskを管理しない"
    // observable = "保存受付後の終了Task未完了、解放後の実SQLite添付と保存成功数"
    // observation_boundary = "public-boundary"
    // scope = "shutdown while screenshot storage is waiting for a shared resource"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ShutdownAlsoWaitsForScreenshotStorage()
    {
        await _screenshots.DisposeAsync();
        var heldStore = new HeldScreenshotStore(_store);
        await using var screenshots = new ScenarioScreenshotService(_capture, heldStore, _platform, _settings,
            NullLogger<ScenarioScreenshotService>.Instance, _notifications, _clock);
        await BeginPreviewAsync();
        await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "画像保存待ちの本文");
        var target = _capture.FreezeScreenshotTarget().Target;
        var pending = screenshots.CaptureAsync();
        await heldStore.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = screenshots.DisposeAsync().AsTask();
        Assert.False(closing.IsCompleted);
        heldStore.Release.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Single(await _store.GetEventScreenshotsAsync(target.CaptureGameId, target.Event));
        Assert.Equal(1, screenshots.Snapshot.SavedCount);
    }

    private sealed class HeldScreenshotStore(IScenarioScreenshotStore inner) : IScenarioScreenshotStore
    {
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ScenarioScreenshot> SaveScreenshotAsync(ScreenshotTarget target, ScreenshotImageData image,
            DateTimeOffset capturedAt, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.SaveScreenshotAsync(target, image, capturedAt, cancellationToken);
        }
        public Task<IReadOnlyList<ScenarioScreenshot>> GetEventScreenshotsAsync(long game, CaptureEventKey key) => inner.GetEventScreenshotsAsync(game, key);
        public Task<byte[]> ReadEventScreenshotPngAsync(long game, CaptureEventKey key, string imageId) => inner.ReadEventScreenshotPngAsync(game, key, imageId);
        public Task<IReadOnlyList<ScenarioScreenshotEventMetadata>> GetEventScreenshotMetadataAsync(long game, IReadOnlyCollection<CaptureEventKey> keys, CancellationToken cancellationToken = default) => inner.GetEventScreenshotMetadataAsync(game, keys, cancellationToken);
        public Task<IReadOnlyDictionary<CaptureEventKey, int>> GetScreenshotCountsAsync(long game, IReadOnlyCollection<CaptureEventKey> keys) => inner.GetScreenshotCountsAsync(game, keys);
        public Task<ScreenshotCleanupResult> CleanupUnreferencedScreenshotsAsync() => inner.CleanupUnreferencedScreenshotsAsync();
        public Task<byte[]> ReadScenarioImageAsync(ScenarioMaterialAccessLevel level, ScenarioMaterialImageRequest request, CancellationToken cancellationToken = default) => inner.ReadScenarioImageAsync(level, request, cancellationToken);
        public Task ExportScenarioArchiveAsync(long game, ScenarioArchiveSelection selection, string output, ScenarioMaterialAccessLevel accessLevel, CancellationToken cancellationToken = default) => inner.ExportScenarioArchiveAsync(game, selection, output, accessLevel, cancellationToken);
    }

    private sealed class FakeHost : ILunaCaptureHost
    {
        public event Action<CaptureSource, string>? Output;
        public event Action<uint, bool>? ConnectionChanged;
        private (TaskCompletionSource Entered, TaskCompletionSource Release)? _hold;
        public (TaskCompletionSource Entered, TaskCompletionSource Release) HoldNextConnection() =>
            (_hold = (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously))).Value;
        public async Task ConnectAsync(int processId)
        {
            if (_hold is { } hold)
            {
                _hold = null;
                hold.Entered.TrySetResult();
                await hold.Release.Task;
            }
            ConnectionChanged?.Invoke((uint)processId, true);
        }
        public void Detach(int processId) => ConnectionChanged?.Invoke((uint)processId, false);
        public IReadOnlyList<CaptureProcess> GetProcesses() => [new(Environment.ProcessId, "test-body", Environment.ProcessPath!, "テスト用ゲーム")];
        public CaptureProcess? GetForegroundProcess() => GetProcesses()[0];
        public void Emit(CaptureSource source, string text) => Output?.Invoke(source, text);
    }

    private sealed class FakePlatform : IScenarioScreenshotPlatform
    {
        public static readonly ScenarioScreenshotFrame Frame = new(1, 1,
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII="), new string('a', 64));
        public CaptureWindow[] Windows = [new((nint)1, Environment.ProcessId, "test-body", "本文窓"), new((nint)2, int.MaxValue, "separate-renderer", "別PID描画窓")];
        public List<CaptureWindow> Captured = [];
        public List<CaptureWindow> GameWindowsCaptured = [];
        public List<ScreenshotHotkey> RegisteredKeys = [];
        public HashSet<ScreenshotHotkey> FailedKeys = [];
        public ScreenshotHotkey? ActiveKey;
        public int Signals;
        public Action? Callback;
        private TaskCompletionSource<ScenarioScreenshotFrame>? _nextFrame;
        private (TaskCompletionSource Entered, TaskCompletionSource<ScenarioScreenshotFrame> Release)? _nextGameFrame;
        public TaskCompletionSource<ScenarioScreenshotFrame> HoldNextFrame() => _nextFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public (TaskCompletionSource Entered, TaskCompletionSource<ScenarioScreenshotFrame> Release) HoldNextGameFrame() =>
            (_nextGameFrame = (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously))).Value;
        public IReadOnlyList<CaptureWindow> ListWindows(int? processId = null) => Windows.Where(x => processId is null || x.ProcessId == processId).ToArray();
        public Task<ScenarioScreenshotFrame> CaptureAsync(CaptureWindow target, CancellationToken cancellationToken = default)
        {
            Captured.Add(target);
            var next = _nextFrame; _nextFrame = null;
            return next?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(Frame);
        }
        public Task<ScenarioScreenshotFrame> CaptureGameWindowAsync(CaptureWindow target, CancellationToken cancellationToken = default)
        {
            GameWindowsCaptured.Add(target);
            if (_nextGameFrame is { } pending)
            {
                _nextGameFrame = null;
                pending.Entered.TrySetResult();
                return pending.Release.Task.WaitAsync(cancellationToken);
            }
            return Task.FromResult(Frame);
        }
        public Task RegisterHotkeyAsync(ScreenshotHotkey hotkey, Action callback)
        {
            RegisteredKeys.Add(hotkey); Callback = null; ActiveKey = null;
            if (FailedKeys.Contains(hotkey)) throw new InvalidOperationException("キーの競合");
            Callback = callback; ActiveKey = hotkey; return Task.CompletedTask;
        }
        public Task UnregisterHotkeyAsync() { Callback = null; ActiveKey = null; return Task.CompletedTask; }
        public void SignalFailure() => Signals++;
        public void Dispose() { }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "停止中の本文と撮影は保存せず、受付済み撮影は元本文へ完了し、再開直後の撮影は旧本文に付かず次の本文から別経路になる"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/34" }
    // fault = "停止中受信を再開後にbackfillする、旧本文を撮影対象に残す、または再開後のcutを失う"
    // observable = "SQLite保存本文とイベント別画像、撮影回数、再開後のraw cut、正式記録設定と状態"
    // observation_boundary = "public-boundary"
    // scope = "pause resume racing accepted screenshot and candidate backfill"
    // lifecycle = "permanent"
    // impact = "停止中の私的な場面を保存したり、別の場面の画像を旧本文に誤関連付けする"
    // distinction = "既存の終了/取得回切替testは同じ接続内の一時停止と再開時backfillを通らない"
    // @end-test-value
    [Fact]
    public async Task PauseDropsTextAndHotkeysUntilANewBodyArrivesAfterResume()
    {
        await BeginPreviewAsync(); await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "停止前");
        var before = _capture.FreezeScreenshotTarget().Target;
        var frame = _platform.HoldNextFrame();
        var accepted = _screenshots.CaptureAsync();
        _capture.PauseRecording();
        Assert.True(_capture.Snapshot.Paused);
        Assert.False(_capture.Snapshot.Recording);
        Assert.True(_capture.Snapshot.Connected);
        _host.Emit(Body, "停止中の本文"); _host.Emit(Name, "停止中の名前");
        await _capture.SaveRulesAsync(_capture.Snapshot.Profile!.Rules);
        Assert.Throws<InvalidOperationException>(() => _capture.FreezeScreenshotTarget());
        frame.SetResult(FakePlatform.Frame); await accepted;
        await _screenshots.CaptureAsync();
        Assert.Single(_platform.Captured);
        _capture.ResumeRecording();
        await _screenshots.CaptureAsync();
        Assert.Single(_platform.Captured);
        _host.Emit(Name, "再開後の名前"); _host.Emit(Body, "");
        await _screenshots.CaptureAsync();
        Assert.Single(_platform.Captured);
        _host.Emit(Body, "再開後");
        var after = _capture.FreezeScreenshotTarget().Target;
        await _screenshots.CaptureAsync(); await _capture.FlushPendingAsync();
        Assert.Equal(2, _platform.Captured.Count);
        Assert.Single(await _store.GetEventScreenshotsAsync(before.CaptureGameId, before.Event));
        Assert.Single(await _store.GetEventScreenshotsAsync(after.CaptureGameId, after.Event));
        Assert.Equal(new[] { "停止前", "再開後" }, (await _store.GetCaptureItemsAsync(before.Event.SessionId, 0)).Select(x => x.Text));
        Assert.Contains(new CapturePosition(after.Event.SessionId, after.Event.Sequence, 0), (await _store.GetScenarioFlowOverridesAsync(after.CaptureGameId)).Cuts);
        Assert.True(_capture.Snapshot.Profile!.Enabled);
        Assert.False(_capture.Snapshot.Paused);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "プレイ中の経路区切りは名前・空本文・重複Sequenceで消費されず次の保存成功本文の直前に永続化し、本文と撮影の記録は続く"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/34" }
    // fault = "名前・空本文・inserted=falseへcutを消費するか区切り操作を記録停止として扱う"
    // observable = "保存されたcut位置、全本文と後続画像、記録中状態"
    // observation_boundary = "public-boundary"
    // scope = "live path break writer boundary"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task LiveBreakWaitsForAnActualBodyAndKeepsRecording()
    {
        await BeginPreviewAsync(); await _capture.ConfirmProcessAsync();
        _host.Emit(Body, "A"); await _capture.FlushPendingAsync();
        _capture.BreakPath();
        var session = _capture.Snapshot.SessionId!.Value;
        await _store.AppendCaptureAsync(session, new(4, DateTimeOffset.UtcNow, Body, "既存の重複Sequence", CaptureRole.Body), new());
        _host.Emit(Name, "名前"); _host.Emit(Body, ""); _host.Emit(Body, "重複なので保存しない"); _host.Emit(Body, "B"); _host.Emit(Body, "C");
        await _screenshots.CaptureAsync(); await _capture.FlushPendingAsync();
        var snapshot = _capture.Snapshot;
        Assert.True(snapshot.Recording);
        Assert.Equal(new[] { "A", "既存の重複Sequence", "B", "C" }, (await _store.GetCaptureItemsAsync(snapshot.SessionId!.Value, 0)).Select(x => x.Text));
        Assert.Equal(new long[] { 1, 5 }, (await _store.GetScenarioFlowOverridesAsync(snapshot.Profile!.Id)).Cuts.Select(x => x.Sequence));
        Assert.Single(await _store.GetEventScreenshotsAsync(snapshot.Profile.Id, new(snapshot.SessionId.Value, 6)));
    }

    private sealed class FakeSettings : IAppSettingsService
    {
        private AppSettings _settings = AppSettings.Default;
        public bool FailNextSave;
        public AppSettings Get() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (FailNextSave) { FailNextSave = false; throw new IOException("設定を書き込めません"); }
            _settings = settings; return Task.CompletedTask;
        }
    }

    private sealed class FakeNotifications : INotificationService
    {
        public List<string> Sent = [];
        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default)
        { Sent.Add(message); return Task.CompletedTask; }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    public async ValueTask DisposeAsync()
    {
        await _screenshots.DisposeAsync();
        await _capture.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
