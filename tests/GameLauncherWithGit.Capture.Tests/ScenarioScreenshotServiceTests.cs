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
    // observable = "サービス保存件数と失敗状態、原文添付読取、PNGファイルの件数"
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
        Assert.Empty(await _store.GetEventScreenshotsAsync(target.CaptureGameId, target.Event));
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
        public Task<IReadOnlyDictionary<CaptureEventKey, int>> GetScreenshotCountsAsync(long game, IReadOnlyCollection<CaptureEventKey> keys) => inner.GetScreenshotCountsAsync(game, keys);
        public Task<ScreenshotCleanupResult> CleanupUnreferencedScreenshotsAsync() => inner.CleanupUnreferencedScreenshotsAsync();
        public Task ExportScenarioArchiveAsync(long game, ScenarioArchiveSelection selection, string output) => inner.ExportScenarioArchiveAsync(game, selection, output);
    }

    private sealed class FakeHost : ILunaCaptureHost
    {
        public event Action<CaptureSource, string>? Output;
        public event Action<uint, bool>? ConnectionChanged;
        public Task ConnectAsync(int processId) { ConnectionChanged?.Invoke((uint)processId, true); return Task.CompletedTask; }
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
        public List<ScreenshotHotkey> RegisteredKeys = [];
        public HashSet<ScreenshotHotkey> FailedKeys = [];
        public ScreenshotHotkey? ActiveKey;
        public int Signals;
        public Action? Callback;
        private TaskCompletionSource<ScenarioScreenshotFrame>? _nextFrame;
        public TaskCompletionSource<ScenarioScreenshotFrame> HoldNextFrame() => _nextFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<CaptureWindow> ListWindows(int? processId = null) => Windows.Where(x => processId is null || x.ProcessId == processId).ToArray();
        public Task<ScenarioScreenshotFrame> CaptureAsync(CaptureWindow target, CancellationToken cancellationToken = default)
        {
            Captured.Add(target);
            var next = _nextFrame; _nextFrame = null;
            return next?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(Frame);
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
