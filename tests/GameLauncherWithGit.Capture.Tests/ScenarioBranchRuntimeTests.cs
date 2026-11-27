using System.Diagnostics;
using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioBranchRuntimeTests : IAsyncDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("scenario-runtime-");
    private readonly SqliteGameLibraryStore _store;
    private readonly SyntheticHost _host = new();
    private readonly FixtureGit _git = new();
    private readonly ScenarioRecordTargetResolver _resolver;
    private readonly ScenarioCaptureService _capture;
    private readonly ScenarioScreenshotService _screenshots;
    private readonly SyntheticScreenshotPlatform _platform = new();
    private readonly PlayRecordingOperations _play;

    public ScenarioBranchRuntimeTests()
    {
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory.FullName, "capture.db"));
        _resolver = new(_store, _store, _git, new FixtureBranchRoots(_store));
        _capture = new(_store, _host, NullLogger<ScenarioCaptureService>.Instance, _resolver);
        var desktop = new SyntheticDesktopServices();
        _screenshots = new(_capture, _store, _platform, desktop, NullLogger<ScenarioScreenshotService>.Instance, desktop);
        _play = new(_capture, _store, _screenshots, _store);
    }

    /// <summary>
    /// The Web read path reports the selected branch, applies both search fields ahead of paging,
    /// and returns image counts only for the requested events. After a branch change the old
    /// display token cannot obtain metadata or text from the new recording root.
    /// </summary>
    [Fact]
    public async Task WebSearchAndImageCountsStayWithinTheDisplayedRecordingRoot()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_directory.FullName, "web-repo")).FullName;
        await RunGitAsync(repo, "init", "--initial-branch=a");
        await RunGitAsync(repo, "commit", "--allow-empty", "-m", "initial");
        await RunGitAsync(repo, "branch", "b");
        await AddGameAsync("game", repo);
        var input = new SyntheticHost();
        await using var capture = new ScenarioCaptureService(_store, input, NullLogger<ScenarioCaptureService>.Instance,
            new ScenarioRecordTargetResolver(_store, _store, _git, _store));
        var desktop = new SyntheticDesktopServices();
        await using var screenshots = new ScenarioScreenshotService(capture, _store, new SyntheticScreenshotPlatform(),
            desktop, NullLogger<ScenarioScreenshotService>.Instance, desktop);
        var web = new PlayRecordingOperations(capture, _store, screenshots, _store);
        await capture.PreparePlayTargetAsync("game");
        await capture.SelectProcessAsync(Environment.ProcessId);
        await capture.SaveRulesAsync(BodyRules);
        await capture.ConfirmProcessAsync();

        var a = Assert.IsType<PlayRecordingState>(await web.GetCurrentStateAsync());
        Assert.Equal("refs/heads/a", a.RecordingBranchRef);
        var emit = input.FreezeOutput();
        emit("match first");
        emit("match second");
        emit("unrelated");
        await capture.FlushPendingAsync();
        var items = (await web.GetItemsAsync("game", a.Target)).Items;
        await web.SetSpeakerAsync("game", a.Target, items[0], "Fixture speaker");
        var filtered = await web.GetItemsAsync("game", a.Target, limit: 1,
            textSearch: "MATCH", speakerSearch: "fixture");
        var row = Assert.Single(filtered.Items);
        Assert.Equal("match first", row.Text);
        Assert.False(filtered.HasMore);

        // The count request carries only the keys of the visible events, not the whole history or any image data.
        var imageKey = new CaptureEventKey(row.SessionId, row.Sequence);
        var emptyKey = new CaptureEventKey(items[1].SessionId, items[1].Sequence);
        var frame = SyntheticScreenshotPlatform.Frame;
        await _store.SaveScreenshotAsync(new(a.Target.ProfileId, imageKey),
            new(frame.Width, frame.Height, frame.PixelHash, frame.PngBytes), DateTimeOffset.UtcNow);
        var counts = await web.GetScreenshotCountsAsync("game", a.Target, [imageKey, emptyKey]);
        Assert.Equal(2, counts.Count);
        Assert.Equal(1, counts[imageKey]);
        Assert.Equal(0, counts[emptyKey]);

        await RunGitAsync(repo, "switch", "b");
        var b = Assert.IsType<PlayRecordingState>(await web.GetCurrentStateAsync());
        Assert.Equal("refs/heads/b", b.RecordingBranchRef);
        Assert.NotEqual(a.Target.RecordId, b.Target.RecordId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => web.GetItemsAsync("game", a.Target, textSearch: "match"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => web.GetScreenshotCountsAsync("game", a.Target, [imageKey]));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "外部のGit枝切替で旧取得の受付済み本文だけを元の履歴に残し、同じ枝に戻っても旧接続と旧編集を再利用しない"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / scenario-history-by-git-branch; scenario-branch-record-vs-profile; play-recording-view-scope-epoch; scenario-branch-pc-runtime (旧接続、HEAD不明)" }
    // fault = "同枝commitで分割する、detached HEADで親へ書く、同PIDまたは同じ記録IDを根拠に旧接続・旧表示を受け入れる"
    // observable = "PC/Web対象の記録ID・取得回・ScopeVersion、画像の旧固定の拒否、各根に保存された本文文字列と話者"
    // observation_boundary = "public-boundary"
    // scope = "same-commit branch switching, same-branch empty commit, detached HEAD and return to a prior branch"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ExternalCheckoutIsolatesOldCallbacksAndEditsAcrossBranchReentry()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_directory.FullName, "repo")).FullName;
        await RunGitAsync(repo, "init", "--initial-branch=a");
        await RunGitAsync(repo, "commit", "--allow-empty", "-m", "initial");
        await RunGitAsync(repo, "branch", "b");
        var profile = await AddGameAsync("game", repo);
        await AddGameAsync("root-a");
        await AddGameAsync("root-b");

        await PrepareAndConnectAsync("game");
        await _capture.ConfirmProcessAsync();
        var a = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        var oldConnection = _host.FreezeOutput();
        oldConnection("a before checkout");
        await _capture.FlushPendingAsync();
        var original = Assert.Single((await _play.GetItemsAsync("game", a.Target)).Items);
        Assert.NotEqual(profile.Id, a.Target.ProfileId);

        await RunGitAsync(repo, "commit", "--allow-empty", "-m", "same branch");
        Assert.Equal(a.Target, (await _play.GetCurrentStateAsync())!.Target);

        var frame = _platform.HoldNextFrame();
        var screenshot = _screenshots.CaptureAsync();
        await frame.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // Prevent the real writer from persisting this accepted item until after checkout.
            await using var writeLock = new SqliteConnection($"Data Source={Path.Combine(_directory.FullName, "capture.db")}");
            await writeLock.OpenAsync();
            await using var transaction = writeLock.BeginTransaction();
            oldConnection("accepted A before checkout");
            await RunGitAsync(repo, "switch", "b");
            oldConnection("late A before refresh");
            await transaction.RollbackAsync();
        }
        finally { frame.Release.TrySetResult(SyntheticScreenshotPlatform.Frame); }
        await screenshot;
        Assert.NotNull(_screenshots.Snapshot.LastFailure);
        Assert.Empty(await _store.GetEventScreenshotsAsync(a.Target.ProfileId, new(original.SessionId, original.Sequence)));
        var b = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.True(b.Recording);
        Assert.NotEqual(a.Target.ProfileId, b.Target.ProfileId);
        Assert.NotEqual(a.Target.SessionId, b.Target.SessionId);
        Assert.NotEqual(a.Target.ScopeVersion, b.Target.ScopeVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.SetSpeakerAsync("game", a.Target, original, "stale"));
        oldConnection("late A after reconnect");
        _host.FreezeOutput()("b body");
        await _capture.FlushPendingAsync();
        Assert.Equal("b body", Assert.Single((await _play.GetItemsAsync("game", b.Target)).Items).RawText);

        await RunGitAsync(repo, "switch", "--detach");
        Assert.Null(await _play.GetCurrentStateAsync());
        Assert.Equal("game", _capture.Snapshot.PlayGameId);
        Assert.Null(await _resolver.GetCurrentRecordIdAsync("game"));
        Assert.Empty(await _store.GetGameCaptureItemsAsync(profile.Id));

        await RunGitAsync(repo, "switch", "a");
        var again = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.True(again.Recording);
        Assert.Equal(a.Target.ProfileId, again.Target.ProfileId);
        Assert.NotEqual(a.Target.ScopeVersion, again.Target.ScopeVersion);
        Assert.NotEqual(a.Target.SessionId, again.Target.SessionId);
        var previous = (await _play.GetItemsAsync("game", again.Target)).Items;
        Assert.Equal(new[] { "a before checkout", "accepted A before checkout" }, previous.Select(x => x.RawText));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.SetSpeakerAsync("game", a.Target, previous[0], "still stale"));
        oldConnection("late a after reentry");
        _host.FreezeOutput()("a second session");
        await _capture.FlushPendingAsync();
        var items = (await _play.GetItemsAsync("game", again.Target)).Items;
        Assert.Equal(new[] { "a before checkout", "accepted A before checkout", "a second session" }, items.Select(x => x.RawText));
        Assert.Null(items[0].Speaker);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "PCの準備・試験接続ではWebの作品を変更せず、正式記録の開始が成功したら切り替え、取得停止では作品を保持する"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / scenario-branch-pc-manual-play-target; local-recording-web-follows-pc-play" }
    // fault = "履歴照会や試験接続で表示先を変更する、取得停止で待機へ戻す、または解除後に旧対象を受け入れる"
    // observable = "各操作後のGetCurrentStateAsyncのGameId・Recording・null、解除後の旧本文一覧要求の拒否"
    // observation_boundary = "public-boundary"
    // scope = "PC trial connection, formal confirmation, recording stop and explicit play release"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ManualTargetIsPublishedOnlyAfterFormalConfirmation()
    {
        var first = await AddGameAsync("first");
        var second = await AddGameAsync("second");
        await PrepareAndConnectAsync("first");
        Assert.Null(await _play.GetCurrentStateAsync());
        await _capture.ConfirmProcessAsync();
        var firstRecording = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.Equal("first", firstRecording.GameId);
        Assert.Equal(first.Id, firstRecording.Target.ProfileId);

        await _play.FinishAsync("first", firstRecording.Target);
        var stillPlaying = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.False(stillPlaying.Recording);
        Assert.Null(stillPlaying.Target.SessionId);
        Assert.Equal(firstRecording.Target.ScopeVersion, stillPlaying.Target.ScopeVersion);
        Assert.Equal(second.Id, await _resolver.GetCurrentRecordIdAsync("second"));
        await PrepareAndConnectAsync("second");
        Assert.Equal("first", (await _play.GetCurrentStateAsync())!.GameId);
        await _capture.SaveRulesAsync(new());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _capture.ConfirmProcessAsync());
        Assert.Equal("first", (await _play.GetCurrentStateAsync())!.GameId);
        await _capture.SaveRulesAsync(BodyRules);
        await _capture.ConfirmProcessAsync();
        var switched = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.Equal("second", switched.GameId);
        Assert.True(switched.Recording);
        Assert.NotEqual(firstRecording.Target.ScopeVersion, switched.Target.ScopeVersion);

        await _capture.ClearPlayTargetAsync("first");
        Assert.Equal("second", (await _play.GetCurrentStateAsync())!.GameId);
        await _capture.ClearPlayTargetAsync("second");
        Assert.Null(await _play.GetCurrentStateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.GetItemsAsync("second", switched.Target));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "起動直後に本体候補がなくても後からWebで開始でき、別作品のPC試験接続には割り込まない"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / local-recording-web-follows-pc-play; scenario-branch-pc-manual-play-target; scenario-branch-pc-runtime (Turn 9149)" }
    // fault = "起動準備を接続済みと同一視してWeb開始を永久に拒む、候補なしで開始する、または別作品の試験接続を奪う"
    // observable = "開始許可、候補なしの拒否、候補出現後の新しい取得回、別作品の試験接続継続とWeb開始拒否"
    // observation_boundary = "public-boundary"
    // scope = "late process discovery after launch and concurrent manual trial setup"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ConfiguredGameCanStartAfterItsBodyAppearsWithoutInterruptingAnotherTrial()
    {
        var profile = await AddGameAsync("game");
        await AddGameAsync("other");
        await _store.SaveCaptureProfileAsync(profile with { BodyExecutablePath = Environment.ProcessPath, Rules = BodyRules });
        _host.ProcessAvailable = false;
        Assert.True(await _capture.BeginGameForLaunchAsync("game", Process.GetProcessById(Environment.ProcessId)));
        var waiting = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.True(waiting.CanStart);
        Assert.Null(waiting.Target.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.StartAsync("game", waiting.Target));

        _host.ProcessAvailable = true;
        await _play.StartAsync("game", (await _play.GetCurrentStateAsync())!.Target);
        var recording = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.True(recording.Recording);
        Assert.NotNull(recording.Target.SessionId);
        await _play.FinishAsync("game", recording.Target);

        await PrepareAndConnectAsync("other");
        var otherTrial = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.Equal("game", otherTrial.GameId);
        Assert.False(otherTrial.CanStart);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.StartAsync("game", otherTrial.Target));
        Assert.Equal("other", _capture.Snapshot.Profile!.GameId);
        Assert.True(_capture.Snapshot.Connected && _capture.Snapshot.Preview);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "起動用プロセスが終わっても後着の本体へ引き継ぎ、先に起動していた同一パスのプロセスや別作品との設定競合では接続しない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 起動用と本体が異なる場合; WithMate Session Graph play-recording-web / play-web-runtime-process-identity" }
    // fault = "起動用プロセス終了を本体終了と見なす、共有実行ファイルの別作品に接続する、または実本体終了後も対象を公開する"
    // observable = "WebのPlayRecordingStateおよびStartAsync、合成ホストへの実接続PID"
    // observation_boundary = "public-boundary"
    // scope = "real OS stub and body lifetimes, synthetic capture host"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitedLauncherHandsOffOnlyToTheNewUnambiguousBody(bool exitsBeforeCaptureBegins)
    {
        using var previousBody = StartBodyProcess();
        using var previousInput = previousBody.StandardInput;
        var path = await WaitForExecutablePathAsync(previousBody);
        var profile = await AddGameAsync("game");
        var other = await AddGameAsync("other");
        await _store.SaveCaptureProfileAsync(profile with { BodyExecutablePath = path, Rules = BodyRules });
        await _store.SaveCaptureProfileAsync(other with { BodyExecutablePath = path, Rules = BodyRules });
        _host.Processes = [new(previousBody.Id, "previous body", path)];

        var starter = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
        };
        foreach (var argument in OperatingSystem.IsWindows() ? new[] { "/d", "/q", "/c", "set /p state=" } : ["-c", "read -r state"])
            starter.ArgumentList.Add(argument);
        var launched = Process.Start(starter)!;
        using var stubInput = launched.StandardInput;
        using var exit = Process.GetProcessById(launched.Id);
        if (!exitsBeforeCaptureBegins) Assert.True(await _capture.BeginGameForLaunchAsync("game", launched));
        await stubInput.WriteLineAsync("done");
        await exit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (exitsBeforeCaptureBegins) Assert.True(await _capture.BeginGameForLaunchAsync("game", launched));
        var waiting = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.Null(waiting.Target.SessionId);
        Assert.Null(_host.ConnectedProcessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.StartAsync("game", waiting.Target));

        // Without explicit disambiguation, even a newer candidate is not assigned to a shared path.
        using var actualBody = StartBodyProcess();
        using var actualInput = actualBody.StandardInput;
        var actualPath = await WaitForExecutablePathAsync(actualBody);
        _host.Processes = [new(actualBody.Id, "current body", actualPath)];
        var competing = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.StartAsync("game", competing.Target));
        Assert.Null(_host.ConnectedProcessId);

        _host.Processes = [new(previousBody.Id, "previous body", path)];
        await _store.SaveCaptureProfileAsync(other);
        var older = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.StartAsync("game", older.Target));
        Assert.Null(_host.ConnectedProcessId);

        _host.Processes = [new(actualBody.Id, "current body", actualPath)];
        var ready = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        await _play.StartAsync("game", ready.Target);
        Assert.True((await _play.GetCurrentStateAsync())!.Recording);
        Assert.Equal(actualBody.Id, _host.ConnectedProcessId);
        await actualInput.WriteLineAsync("done");
        await actualBody.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await _play.GetCurrentStateAsync());
    }

    private static Process StartBodyProcess()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/bash")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
        };
        foreach (var argument in OperatingSystem.IsWindows()
            ? new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::ReadLine() > $null" }
            : ["-c", "read -r state"])
            start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task<string> WaitForExecutablePathAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (process.MainModule?.FileName is { } path) return path;
            await Task.Delay(25, timeout.Token);
        }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "取得終了後の同一PID再開で遅着した旧切断は、新しい正常な取得回を終了させない"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / scenario-branch-luna-drain-contract; play-web-runtime-process-identity" }
    // fault = "旧接続を待つ前に新しい切断通知ハンドラーを購読する"
    // observable = "同一PIDに対するWeb記録開始成功、新しい取得回、接続中状態"
    // observation_boundary = "public-boundary"
    // scope = "same-PID explicit restart with a synthetic late disconnect"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ReopeningTheSamePidDoesNotReceiveThePreviousConnectionsDisconnect()
    {
        await AddGameAsync("game");
        await PrepareAndConnectAsync("game");
        await _capture.ConfirmProcessAsync();
        var first = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        _host.DeferDisconnect = true;
        await _play.FinishAsync("game", first.Target);
        var stopped = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        await _play.StartAsync("game", stopped.Target);
        var reopened = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.True(reopened.Recording);
        Assert.NotNull(reopened.Target.SessionId);
        Assert.NotEqual(first.Target.SessionId, reopened.Target.SessionId);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "起動済みゲームは未記録でもPC対象であり、同じ記録IDに戻っても旧表示は使えず、実プロセス終了で対象を解除する"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / scenario-branch-pc-manual-play-target; play-recording-view-scope-epoch" }
    // fault = "取得回が無い時にゲームを公開しない、同じ枝に戻れば無取得の旧ボタンを受け付ける、または終了後も旧履歴を公開する"
    // observable = "記録開始前のGameIdとnull SessionId、枝往復で維持された記録IDと新ScopeVersion、旧一覧の拒否とOS終了後の待機"
    // observation_boundary = "public-boundary"
    // scope = "successful launch with capture disabled, same-commit ABA checkout and real process exit"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task IdlePlayTargetRejectsOldTokenAfterRapidReentryAndClearsOnProcessExit()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_directory.FullName, "repo")).FullName;
        await RunGitAsync(repo, "init", "--initial-branch=a");
        await RunGitAsync(repo, "commit", "--allow-empty", "-m", "initial");
        await RunGitAsync(repo, "branch", "b");
        var profile = await AddGameAsync("game", repo);
        await AddGameAsync("root-a");
        await AddGameAsync("root-b");
        await _store.SaveCaptureProfileAsync(profile with { Enabled = false });

        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "sleep")
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in OperatingSystem.IsWindows() ? new[] { "/d", "/c", "ping -n 6 127.0.0.1 >nul" } : ["5"])
            info.ArgumentList.Add(argument);
        var process = Process.Start(info)!;
        using var exit = Process.GetProcessById(process.Id);
        Assert.False(await _capture.BeginGameForLaunchAsync("game", process));
        var a = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.Equal("game", a.GameId);
        Assert.Null(a.Target.SessionId);

        await RunGitAsync(repo, "switch", "b");
        await RunGitAsync(repo, "switch", "a");
        var again = Assert.IsType<PlayRecordingState>(await _play.GetCurrentStateAsync());
        Assert.Equal(a.Target.ProfileId, again.Target.ProfileId);
        Assert.Null(again.Target.SessionId);
        Assert.NotEqual(a.Target.ScopeVersion, again.Target.ScopeVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.GetItemsAsync("game", a.Target));
        Assert.Empty((await _play.GetItemsAsync("game", again.Target)).Items);

        await exit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await _play.GetCurrentStateAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "HEAD不明時もPCの明示固定履歴は所有が一致する指定範囲だけ操作でき、現在追従や他作品の範囲は書けない"
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / scenario-branch-native-history-selection; scenario-branch-desktop-history-tokens" }
    // fault = "固定根を現在枝や親根に置換する、HEAD不明時に追従を親へフォールバックする、または他作品の枝を許す"
    // observable = "追従と他作品の操作拒否、固定操作で指定した記録根だけのSQLite保存結果、親と別枝の未更新"
    // observation_boundary = "component-behavior"
    // scope = "desktop-only fixed record mutation versus following mode with detached HEAD"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task DesktopFixedHistoryRequiresOwnershipAndDoesNotFallBackToCurrentRoot()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_directory.FullName, "repo")).FullName;
        await RunGitAsync(repo, "init", "--initial-branch=a");
        await RunGitAsync(repo, "commit", "--allow-empty", "-m", "initial");
        await RunGitAsync(repo, "branch", "b");
        await RunGitAsync(repo, "switch", "--detach");
        var profile = await AddGameAsync("game", repo);
        var a = await AddGameAsync("root-a");
        var b = await AddGameAsync("root-b");
        var other = await AddGameAsync("other");
        var orphan = await AddGameAsync("orphan");
        await _store.DeleteAsync("orphan");
        Assert.Null((await _store.GetCaptureProfilesAsync()).Single(x => x.Id == orphan.Id).GameId);

        var following = PlayRecordingOperations.DesktopHistoryTarget(_capture.Snapshot, "game", profile.Id);
        var fixedBranch = PlayRecordingOperations.DesktopHistoryTarget(_capture.Snapshot, profile.Id, a.Id);
        var otherOwner = PlayRecordingOperations.DesktopHistoryTarget(_capture.Snapshot, other.Id, a.Id);
        var fixedOrphan = PlayRecordingOperations.DesktopHistoryTarget(_capture.Snapshot, orphan.Id, orphan.Id);
        async Task RecordChangeAsync(long root)
        {
            var session = await _store.BeginCaptureSessionAsync(root);
            await _store.AppendCaptureAsync(session, new(1, DateTimeOffset.UtcNow,
                new(0, 100, 200, 0, "body-hook", "body"), "fixed history", CaptureRole.Body, null), new());
            await _store.EndCaptureSessionAsync(session);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.RunDesktopHistoryAsync(following, RecordChangeAsync));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _play.RunDesktopHistoryAsync(otherOwner, RecordChangeAsync));
        await _play.RunDesktopHistoryAsync(fixedBranch, RecordChangeAsync);
        Assert.Equal("fixed history", Assert.Single(await _store.GetGameCaptureItemsAsync(a.Id)).RawText);
        await _play.RunDesktopHistoryAsync(fixedOrphan, RecordChangeAsync);
        Assert.Single(await _store.GetGameCaptureItemsAsync(orphan.Id));
        await RunGitAsync(repo, "switch", "b");
        await _play.RunDesktopHistoryAsync(fixedBranch, RecordChangeAsync);
        Assert.Equal(2, (await _store.GetGameCaptureItemsAsync(a.Id)).Count);
        Assert.Empty(await _store.GetGameCaptureItemsAsync(profile.Id));
        Assert.Empty(await _store.GetGameCaptureItemsAsync(b.Id));
    }

    private async Task<CaptureProfile> AddGameAsync(string id, string? repository = null)
    {
        await _store.UpsertAsync(new(id, id, "fixture.exe", repository, null, null, GameCardStatus.Unknown));
        return await _store.GetOrCreateCaptureProfileAsync(id);
    }

    private async Task PrepareAndConnectAsync(string gameId)
    {
        await _capture.PreparePlayTargetAsync(gameId);
        await _capture.SelectProcessAsync(Environment.ProcessId);
        await _capture.SaveRulesAsync(BodyRules);
    }

    private static CaptureRules BodyRules => new() { Selections = [new("body-hook", "body", 200, 0, CaptureRole.Body)] };

    private async Task RunGitAsync(string repository, params string[] arguments)
    {
        var result = await _git.RunAsync(repository, arguments);
        Assert.True(result.IsSuccess, result.StandardError);
    }

    public async ValueTask DisposeAsync()
    {
        await _screenshots.DisposeAsync();
        await _capture.DisposeAsync();
        await _store.DisposeAsync();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory.FullName, "capture.db")};Foreign Keys=True");
        SqliteConnection.ClearPool(connection);
        _directory.Delete(recursive: true);
    }

    private sealed class SyntheticDesktopServices : IAppSettingsService, INotificationService
    {
        public AppSettings Get() => AppSettings.Default;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SyntheticScreenshotPlatform : IScenarioScreenshotPlatform
    {
        public static readonly ScenarioScreenshotFrame Frame = new(1, 1,
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII="), new string('a', 64));
        private (TaskCompletionSource Entered, TaskCompletionSource<ScenarioScreenshotFrame> Release) _pending;
        public (TaskCompletionSource Entered, TaskCompletionSource<ScenarioScreenshotFrame> Release) HoldNextFrame() =>
            _pending = (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
        public IReadOnlyList<CaptureWindow> ListWindows(int? processId = null) => [new(1, Environment.ProcessId, "synthetic", "fixture")];
        public Task<ScenarioScreenshotFrame> CaptureAsync(CaptureWindow target, CancellationToken cancellationToken = default)
        {
            _pending.Entered.SetResult();
            return _pending.Release.Task.WaitAsync(cancellationToken);
        }
        public Task RegisterHotkeyAsync(ScreenshotHotkey hotkey, Action callback) => Task.CompletedTask;
        public Task UnregisterHotkeyAsync() => Task.CompletedTask;
        public void SignalFailure() { }
        public void Dispose() { }
    }

    // Capture the actual callback delegate before reconnect so the old callback can arrive at a controlled time.
    private sealed class SyntheticHost : ILunaCaptureHost
    {
        public event Action<CaptureSource, string>? Output;
        public event Action<uint, bool>? ConnectionChanged;
        private static readonly CaptureSource Source = new((uint)Environment.ProcessId, 100, 200, 0, "body-hook", "body");
        public bool ProcessAvailable { get; set; } = true;
        public IReadOnlyList<CaptureProcess>? Processes { get; set; }
        public int? ConnectedProcessId { get; private set; }
        public bool DeferDisconnect { get; set; }
        private int? _deferredDisconnect;
        public Action<string> FreezeOutput()
        {
            var handler = Output ?? throw new InvalidOperationException("No synthetic capture connection.");
            return text => handler(Source, text);
        }
        public Task WaitForPreviousConnectionAsync(int processId)
        {
            if (_deferredDisconnect == processId)
            {
                _deferredDisconnect = null;
                ConnectionChanged?.Invoke((uint)processId, false);
            }
            return Task.CompletedTask;
        }
        public async Task ConnectAsync(int processId)
        {
            await WaitForPreviousConnectionAsync(processId);
            ConnectedProcessId = processId;
            ConnectionChanged?.Invoke((uint)processId, true);
        }
        public void Detach(int processId)
        {
            if (DeferDisconnect) _deferredDisconnect = processId;
            else ConnectionChanged?.Invoke((uint)processId, false);
        }
        public Task<bool> DetachAndDrainAsync(int processId) { Detach(processId); return Task.FromResult(true); }
        public IReadOnlyList<CaptureProcess> GetProcesses() => Processes
            ?? (ProcessAvailable ? [new(Environment.ProcessId, "synthetic", Environment.ProcessPath!)] : []);
        public CaptureProcess? GetForegroundProcess() => null;
    }

    // Synthetic roots isolate runtime routing from the branch storage implementation. Captured items still use real SQLite.
    private sealed class FixtureBranchRoots(IScenarioCaptureStore store) : IScenarioHistoryStore
    {
        public async Task<long> GetOrCreateBranchRecordIdAsync(long profileId, string repositoryKey, string branchRef)
        {
            Assert.True(Path.IsPathFullyQualified(repositoryKey));
            var id = branchRef switch { "refs/heads/a" => "root-a", "refs/heads/b" => "root-b", _ => throw new ArgumentException(branchRef) };
            return (await store.FindCaptureProfileAsync(id))!.Id;
        }

        public async Task<IReadOnlyList<ScenarioHistoryScope>> GetScenarioHistoryScopesAsync(long profileId)
        {
            if ((await store.FindCaptureProfileAsync("game"))?.Id != profileId) return [new(profileId, profileId, null, null)];
            var a = (await store.FindCaptureProfileAsync("root-a"))!.Id;
            var b = (await store.FindCaptureProfileAsync("root-b"))!.Id;
            return [new(profileId, profileId, null, null), new(a, profileId, "fixture", "refs/heads/a"), new(b, profileId, "fixture", "refs/heads/b")];
        }
    }

    private sealed class FixtureGit : IGitService
    {
        public Task<GitCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            var info = new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryPath, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                info.Environment.Remove(key);
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            info.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            info.Environment["GIT_TEMPLATE_DIR"] = "";
            foreach (var argument in new[] { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid" }.Concat(arguments))
                info.ArgumentList.Add(argument);
            using var git = Process.Start(info)!;
            var stdout = git.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = git.StandardError.ReadToEndAsync(cancellationToken);
            await git.WaitForExitAsync(cancellationToken);
            return new(git.ExitCode, await stdout, await stderr);
        }
    }
}
