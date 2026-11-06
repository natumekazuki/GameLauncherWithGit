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
    private readonly ScenarioCaptureService _capture;
    private readonly ScenarioScreenshotService _screenshots;
    private static readonly CaptureSource Body = new((uint)Environment.ProcessId, 100, 200, 0, "body-hook", "body");
    private static readonly CaptureSource Name = Body with { Address = 101, HookCode = "name-hook", HookName = "name" };

    public ScenarioScreenshotServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
        _capture = new(_store, _host, NullLogger<ScenarioCaptureService>.Instance);
        _screenshots = new(_capture, _store, _platform, new FakeSettings(), NullLogger<ScenarioScreenshotService>.Instance);
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
        public Action? Callback;
        private TaskCompletionSource<ScenarioScreenshotFrame>? _nextFrame;
        public TaskCompletionSource<ScenarioScreenshotFrame> HoldNextFrame() => _nextFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<CaptureWindow> ListWindows(int? processId = null) => Windows.Where(x => processId is null || x.ProcessId == processId).ToArray();
        public Task<ScenarioScreenshotFrame> CaptureAsync(CaptureWindow target, CancellationToken cancellationToken = default)
        {
            Captured.Add(target);
            var next = _nextFrame; _nextFrame = null;
            return next?.Task ?? Task.FromResult(Frame);
        }
        public Task RegisterHotkeyAsync(ScreenshotHotkey hotkey, Action callback) { RegisteredKeys.Add(hotkey); Callback = callback; return Task.CompletedTask; }
        public Task UnregisterHotkeyAsync() { Callback = null; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class FakeSettings : IAppSettingsService
    {
        private AppSettings _settings = AppSettings.Default;
        public AppSettings Get() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { _settings = settings; return Task.CompletedTask; }
    }

    public async ValueTask DisposeAsync()
    {
        await _screenshots.DisposeAsync();
        await _capture.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
