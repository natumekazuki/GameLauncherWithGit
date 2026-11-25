using System.Globalization;
using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Domain.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Git.Tests;

public sealed class SyncCadenceTests
{
    // @test-value v2
    // kind = "invariant"
    // claim = "旧設定の間隔未指定は0で、明示値は0から1440分に収めて保存・再読込できる"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract" }
    // fault = "欠落を無効または非ゼロと扱う、あるいは不正な境界値をそのまま実行計画に渡す"
    // observable = "実AppSettingsServiceによる旧JSON・保存後JSONの読出し値"
    // observation_boundary = "public-boundary"
    // scope = "saved global Git cadence settings compatibility"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task LegacySettingsReadAsZeroAndSavedIntervalsAreClamped()
    {
        var directory = Directory.CreateTempSubdirectory("sync-settings-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "settings.json"),
                """{"syncDebounceSeconds":42,"showCardTitle":false}""");
            var logger = NullLogger<AppSettingsService>.Instance;
            var store = new AppSettingsService(logger, directory.FullName);
            var legacy = store.Get();
            Assert.Equal((0, 0, 42, false), (legacy.AutoCommitIntervalMinutes, legacy.AutoPushIntervalMinutes,
                legacy.SyncDebounceSeconds, legacy.ShowCardTitle));
            await store.SaveAsync(legacy with { AutoCommitIntervalMinutes = 1500, AutoPushIntervalMinutes = -1 });
            var reloaded = new AppSettingsService(logger, directory.FullName).Get();
            Assert.Equal((1440, 0, 42, false), (reloaded.AutoCommitIntervalMinutes, reloaded.AutoPushIntervalMinutes,
                reloaded.SyncDebounceSeconds, reloaded.ShowCardTitle));
        }
        finally
        {
            File.Delete(Path.Combine(directory.FullName, "settings.json"));
            directory.Delete();
        }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "0/0は監視登録時にGitを起動せず、変更検知後に従来のデバウンスと同期手順を使う"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract; docs/要件定義.md §8.2・§8.3" }
    // fault = "旧設定でも登録直後に同期する、または既存のfetch/pull・commit・push経路を通さない"
    // observable = "Git代替のコマンド順と同期履歴"
    // observation_boundary = "public-boundary"
    // scope = "legacy automatic sync with zero cadence settings"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void ZeroCadenceKeepsLegacyDebouncedSync()
    {
        using var f = new Fixture(commit: 0, push: 0) { Dirty = true };
        f.Watch();
        Assert.Empty(f.Commands);
        f.Change();
        f.Advance(seconds: 9);
        Assert.Empty(f.Commands);
        f.Advance(seconds: 1);

        Assert.Equal(new[] { "fetch", "branch --show-current", "config --get branch.sample.remote",
            "config --get branch.sample.merge", "pull --rebase --autostash origin sample",
            "add -A", "status --porcelain", "commit", "push" }, f.Commands);
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, Assert.Single(f.History).Status);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "変更や監視再登録でコミット期限を延ばさず、以後の変更がなくてもHome離脱中に独立したpush期限で送信する"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract; game-sync-cadence-watch-lifetime" }
    // fault = "変更で期限を更新する、コミットをpush期限まで待つ、監視解除で保持した期限を捨てる、または待機を成功にする"
    // observable = "仮想時刻ごとのcommit・push呼出し、送信前の履歴・通知なし、送信後の成功履歴"
    // observation_boundary = "public-boundary"
    // scope = "independent deadlines through watcher re-registration and Home departure"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void CommitAndPushDeadlinesDoNotSlideOrDependOnAWatcher()
    {
        using var f = new Fixture(commit: 2, push: 3) { Dirty = true };
        f.Watch();
        f.Advance(seconds: 105);
        f.Unwatch();
        f.Watch();
        f.Change();
        f.Advance(seconds: 14);
        f.Change(); // New debounce would otherwise run beyond the original commit deadline.
        Assert.Equal(0, f.Commits);
        f.Advance(seconds: 1);

        Assert.Equal(1, f.Commits);
        Assert.Equal(0, f.Pushes);
        Assert.Empty(f.History);
        Assert.Empty(f.Notifications);
        f.Unwatch();
        f.Games = [f.Game("other", f.Repository + Path.DirectorySeparatorChar)];
        f.Advance(seconds: 179);
        Assert.Equal(0, f.Pushes);
        f.Advance(seconds: 1);

        Assert.Equal(1, f.Commits);
        Assert.Equal(1, f.Pushes);
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, Assert.Single(f.History).Status);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "作業ツリー変更が継続してデバウンス完了を待てなくても、最初の検知からのコミット期限は維持される"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract" }
    // fault = "最初のローカルGit確認をデバウンス終了まで遅らせ、変更が続く間はコミット期限を起動しない"
    // observable = "デバウンス以下の間隔で監視通知した場合の期限直前と期限到達のcommit/push回数"
    // observation_boundary = "public-boundary"
    // scope = "commit deadline with continuous change detection"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void ContinuousWatcherEventsCannotPostponeFirstCommit()
    {
        using var f = new Fixture(commit: 1, push: 0);
        f.Watch();
        f.Dirty = true;
        f.Change();
        for (var i = 0; i < 6; i++)
        {
            f.Advance(seconds: 9);
            f.Change();
        }
        f.Advance(seconds: 5);
        Assert.Equal((0, 0), (f.Commits, f.Pushes));
        f.Advance(seconds: 1);
        Assert.Equal((1, 1), (f.Commits, f.Pushes));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "起動時の未送信は別のコミット期限を待たずに送信し、作業が残る間やクリーンかつ送信済みのときは成功履歴を作らない"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract" }
    // fault = "起動時に作業ツリー変更だけを調べる、または全登録を無条件に送信済みにする"
    // observable = "無変更時のGit書込と履歴の不在、既存commit時刻で決まるpushの発火時点"
    // observation_boundary = "public-boundary"
    // scope = "startup reconciliation of existing Git state without a new ledger"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StartupRecoversOutgoingCommitsWithoutCreatingOne(bool outgoing, bool dirty)
    {
        using var f = new Fixture(commit: 10, push: 5) { Ahead = outgoing, Dirty = dirty };
        f.Oldest = f.Clock.GetUtcNow().AddMinutes(-4);
        f.Watch();
        f.Advance(seconds: 59);
        Assert.Equal((0, 0), (f.Commits, f.Pushes));
        Assert.Empty(f.History);
        f.Advance(seconds: 1);
        Assert.Equal((0, outgoing ? 1 : 0), (f.Commits, f.Pushes));
        Assert.Equal(outgoing && !dirty ? 1 : 0, f.History.Count);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "保留中に間隔を両方0へ変更すると、次の登録確認で保留を処理し古い期限では二重実行しない"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract; game-sync-cadence-watch-lifetime" }
    // fault = "既存保留を0設定で捨てる、または更新した計画の後も旧期限が有効なまま"
    // observable = "次の登録確認でのcommit・push、成功履歴、旧期限通過時の追加Git呼出しなし"
    // observation_boundary = "public-boundary"
    // scope = "rescheduling already-pending work when settings change"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void NextPlanAppliesZeroToAlreadyPendingWorkWithoutReplayingOldDeadline()
    {
        using var f = new Fixture(commit: 10, push: 10) { Dirty = true };
        f.Watch();
        f.Advance(seconds: 30);
        Assert.Equal((0, 0), (f.Commits, f.Pushes));
        f.Settings = AppSettings.Default;
        f.Unwatch();
        f.Watch();
        Assert.Equal((1, 1), (f.Commits, f.Pushes));
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, Assert.Single(f.History).Status);
        var commands = f.Commands.Count;
        f.Advance(seconds: 600);
        Assert.Equal(commands, f.Commands.Count);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "期限実行前に登録を再確認し、読めないときは再試行、全登録が消えたときはGitを実行しない"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-watch-lifetime" }
    // fault = "登録解除後に期限からGitを実行する、またはDB読取失敗を未登録として期限を捨てる"
    // observable = "照合失敗時の失敗履歴、期限/再試行でのGit呼出し数、DB復帰後のcommit"
    // observation_boundary = "public-boundary"
    // scope = "registration authority and retry at deadline"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeadlineRechecksRegistrationAndDoesNotTreatDbFailureAsRemoval(bool readFails)
    {
        using var f = new Fixture(commit: 1, push: 0) { Dirty = true };
        f.Watch();
        f.Unwatch();
        var before = f.Commands.Count;
        if (readFails)
        {
            f.FailLibraryRead = true;
        }
        else
        {
            f.Games = [f.Game("changed", f.Repository + "-new")];
        }
        f.Advance(seconds: 60);
        Assert.Equal(before, f.Commands.Count);

        if (readFails)
        {
            Assert.Equal(RepositorySyncHistoryStatus.Failed, Assert.Single(f.History).Status);
            f.FailLibraryRead = false;
            f.Advance(seconds: 5);
            Assert.Equal(1, f.Commits);
            Assert.Equal(RepositorySyncHistoryStatus.Succeeded, f.History.Last().Status);
        }
        else
        {
            f.Advance(seconds: 600);
            Assert.Equal(before, f.Commands.Count);
            Assert.Empty(f.History);
        }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "同一worktreeの別名は期限・受付・停止を共有し、付替えで登録を失った旧物理先にはGitを実行しない"
    // oracle = { type = "contract", ref = "Session Graph game-git-repository-identity-boundary; game-sync-cadence-watch-lifetime" }
    // fault = "watcher、DB、Trayが異なる文字列で同一worktreeを別キューとして扱う"
    // observable = "物理キーだけで動くGit代替、同一期限のcommit/push回数、別名からの停止時受付拒否"
    // observation_boundary = "public-boundary"
    // scope = "queue and registration identity with a shared path resolver"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AliasSharesRegistrationDeadlineAndPauseWithPhysicalKey(bool retargeted)
    {
        var alias = Path.Combine(Path.GetTempPath(), "cadence-alias");
        var target = Path.Combine(Path.GetTempPath(), "cadence-repository");
        using var f = new Fixture(commit: 1, push: 0, resolveDirectoryPath: path =>
            path == alias ? target : path) { Dirty = true };
        f.Games = [f.Game("aliased", alias)];
        f.Watch();
        f.Register(alias, alias);
        var before = f.Commands.Count;
        if (retargeted)
        {
            target += "-new";
            f.Advance(seconds: 60);
            Assert.Equal(before, f.Commands.Count);
            Assert.Empty(f.History);
            return;
        }
        f.Advance(seconds: 60);
        Assert.Equal((1, 1), (f.Commits, f.Pushes));

        f.Dirty = true;
        f.Conflict = true;
        Assert.True(f.Sync.TryQueueRepositorySyncNow(alias));
        Assert.Equal(RepositorySyncState.ErrorPaused, f.State);
        Assert.False(f.Sync.TryQueueRepositorySyncNow(f.Repository));
        Assert.Equal((1, 1), (f.Commits, f.Pushes));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "手動と明示再開は期限を待たず、pull/自動stash復元の競合で停止したら監視・期限・一括同期で解除しない"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract; app-wide-sync-queue-vs-completion" }
    // fault = "手動も分間隔を待つ、または競合した作業を自動で続行しステージ・送信する"
    // observable = "即時受付・停止状態・履歴、停止前後と再開でのGitのcommit/push呼出し"
    // observation_boundary = "public-boundary"
    // scope = "manual override and conflict safety with cadence enabled"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualAndExplicitResumeBypassCadenceButNotConflictSafety(bool autostashConflict)
    {
        using var f = new Fixture(commit: 10, push: 10)
            { Dirty = true, Conflict = !autostashConflict, ConflictAfterPull = autostashConflict };
        f.Watch();
        Assert.True(f.Sync.TryQueueRepositorySyncNow(f.Repository));
        Assert.Equal(RepositorySyncState.ErrorPaused, f.State);
        Assert.DoesNotContain("add -A", f.Commands);
        Assert.Equal(RepositorySyncHistoryStatus.Paused, Assert.Single(f.History).Status);
        var before = f.Commands.Count;
        f.Change();
        f.Advance(seconds: 601);
        Assert.False(f.Sync.TryQueueRepositorySyncNow(f.Repository));
        Assert.Equal(before, f.Commands.Count);

        f.Conflict = false;
        f.ConflictAfterPull = false;
        f.ConflictedIndex = false;
        await f.Sync.ResumeRepositorySyncAsync(f.Repository).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((1, 1), (f.Commits, f.Pushes));
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, f.History.Last().Status);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "送信先が不明でGitが現在ブランチの送信を確認できない場合は成功にせず、pushの再試行前にもコミットできる"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract" }
    // fault = "不明を未送信ゼロとする、他ブランチのpush成功を流用する、またはpush失敗の待機で別のコミットを止める"
    // observable = "確認できない送信の失敗履歴と通知、backoff中のcommit、期限後の現ブランチpushと復旧通知"
    // observation_boundary = "public-boundary"
    // scope = "unconfirmed push and independent commit while push retries"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void UnknownPushTargetRequiresProofAndPushRetryDoesNotHoldCommit()
    {
        using var f = new Fixture(commit: 0, push: 1, retry: 30) { TargetKnown = false, PushReportsBranch = false };
        f.Watch();
        Assert.Empty(f.History);
        f.Advance(seconds: 60);
        Assert.Equal(RepositorySyncHistoryStatus.Failed, Assert.Single(f.History).Status);
        f.Dirty = true;
        f.Change();
        f.Advance(seconds: 10);
        Assert.Equal((1, 1), (f.Commits, f.Pushes));
        Assert.Single(f.History);
        Assert.Equal(new[] { "同期に失敗しました" }, f.Notifications);
        f.PushReportsBranch = true;
        f.Advance(seconds: 20);
        Assert.Equal((1, 2), (f.Commits, f.Pushes));
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, f.History.Last().Status);
        Assert.Equal(new[] { "同期に失敗しました", "同期が復旧しました" }, f.Notifications);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "Gitが実行取消を返した起動済み同期は処理を続けず、競合停止や失敗再試行に変えない"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-core-worker; src/GameLauncherWithGit/Infrastructure/Abstractions/IGitService.cs" }
    // fault = "Gitの取消を失敗または成功として扱い後続コマンドや自動再試行を実行する"
    // observable = "取消後のGit呼出し、状態と同期履歴"
    // observation_boundary = "public-boundary"
    // scope = "cancellation of an active scheduled sync"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ActiveCancellationDoesNotRecordFailureOrRetry()
    {
        using var f = new Fixture(commit: 1, push: 1) { Dirty = true };
        using var cancel = new CancellationTokenSource();
        f.CancelOnFetch = cancel;
        await f.Sync.ResumeRepositorySyncAsync(f.Repository, cancel.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(RepositorySyncState.Idle, f.State);
        var before = f.Commands.Count;
        f.Advance(seconds: 600);
        Assert.Equal(before, f.Commands.Count);
        Assert.Equal((0, 0), (f.Commits, f.Pushes));
        Assert.Empty(f.History);
    }

    private sealed class Fixture : IDisposable, IRepositoryWatcherService, IGameLibraryStore, IGitService,
        IAppSettingsService, INotificationService, ITrayService, ILogAccessService, IRepositorySyncHistoryStore
    {
        private const string TrackingRef = "refs/remotes/origin/sample";
        private readonly RepositoryStateStore _states = new();
        public string Repository { get; } = Path.Combine(Path.GetTempPath(), "cadence-repository");
        public ManualClock Clock { get; } = new();
        public SyncOrchestrator Sync { get; }
        public List<string> Commands { get; } = [];
        public List<RepositorySyncHistoryItem> History { get; } = [];
        public List<string> Notifications { get; } = [];
        public IReadOnlyList<GameCardItem> Games { get; set; }
        public bool Dirty { get; set; }
        public bool Ahead { get; set; }
        public DateTimeOffset Oldest { get; set; }
        public bool Conflict { get; set; }
        public bool ConflictAfterPull { get; set; }
        public bool ConflictedIndex { get; set; }
        public AppSettings Settings { get; set; }
        public bool TargetKnown { get; set; } = true;
        public bool PushReportsBranch { get; set; } = true;
        public bool FailLibraryRead { get; set; }
        public CancellationTokenSource? CancelOnFetch { get; set; }
        public int Commits { get; private set; }
        public int Pushes { get; private set; }
        public RepositorySyncState State => _states.GetStateOrDefault(Repository);
        private bool _watching;

        public Fixture(int commit, int push, int retry = 5, Func<string, string>? resolveDirectoryPath = null)
        {
            Settings = AppSettings.Default with
            {
                AutoCommitIntervalMinutes = commit, AutoPushIntervalMinutes = push, SyncRetryInitialSeconds = retry
            };
            Games = [Game("fixture", Repository)];
            Oldest = Clock.GetUtcNow();
            Sync = new SyncOrchestrator(_states, this, this, this, this, this, this, this,
                NullLogger<SyncOrchestrator>.Instance, Clock.Delay, Clock, this, resolveDirectoryPath);
        }

        public GameCardItem Game(string id, string repository) => new(id, id, "", repository, null, null, GameCardStatus.Unknown);
        public void Advance(int seconds) => Clock.Advance(TimeSpan.FromSeconds(seconds));
        public void Watch() => Register(Repository, Repository);
        public void Unwatch() => Unregister(Repository);
        public void Change()
        {
            if (_watching) RepositoryChanged?.Invoke(this, new(Repository));
        }
        public event EventHandler<RepositoryChangedEventArgs>? RepositoryChanged;
        public event EventHandler<RepositoryChangedEventArgs>? RepositoryRegistered;
        public void Register(string repositoryId, string watchPath)
        {
            _watching = true;
            RepositoryRegistered?.Invoke(this, new(repositoryId));
        }
        public void Unregister(string repositoryId) => _watching = false;

        public Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Repository, repositoryPath);
            var command = arguments[0] == "commit" ? "commit" : string.Join(' ', arguments);
            Commands.Add(command);
            GitCommandResult result = command switch
            {
                "status --porcelain" => new(0, ConflictedIndex ? "UU save.dat\n" : Dirty ? " M save.dat\n" : "", ""),
                "rev-parse --verify --quiet HEAD" => new(0, "abc", ""),
                "symbolic-ref --quiet HEAD" => new(0, "refs/heads/sample\n", ""),
                "for-each-ref --format=%(push)%09%(push:remotename) refs/heads/sample" =>
                    new(0, TargetKnown ? TrackingRef + "\torigin\n" : "\t\n", ""),
                "remote get-url --all -- origin" or "remote get-url --push --all -- origin" => new(0, "fixture-remote\n", ""),
                "show-ref --verify --quiet " + TrackingRef => new(0, "", ""),
                "log --format=%ct " + TrackingRef + "..HEAD" =>
                    new(0, Ahead ? Oldest.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "\n" : "", ""),
                "rev-list --count " + TrackingRef + "..HEAD" => new(0, Ahead ? "1\n" : "0\n", ""),
                "branch --show-current" => new(0, "sample", ""),
                "config --get branch.sample.remote" => new(0, "origin", ""),
                "config --get branch.sample.merge" => new(0, "refs/heads/sample", ""),
                "pull --rebase --autostash origin sample" when Conflict => new(1, "CONFLICT", ""),
                "pull --rebase --autostash origin sample" when ConflictAfterPull => ApplyConflictingAutostash(),
                "fetch" when CancelOnFetch is { } cancel => Cancel(cancel),
                "fetch" or "pull --rebase --autostash origin sample" or "add -A" => new(0, "", ""),
                "commit" => Commit(),
                "push" or "push --porcelain" => Push(),
                _ => throw new NotSupportedException(command)
            };
            return Task.FromResult(result);
        }

        private GitCommandResult ApplyConflictingAutostash()
        {
            ConflictedIndex = true;
            return new(0, "", "");
        }

        private static GitCommandResult Cancel(CancellationTokenSource token)
        {
            token.Cancel();
            return new(-2, "", "canceled");
        }

        private GitCommandResult Commit()
        {
            Commits++;
            Dirty = false;
            if (!Ahead) Oldest = Clock.GetUtcNow();
            Ahead = true;
            return new(0, "", "");
        }

        private GitCommandResult Push()
        {
            Pushes++;
            if (PushReportsBranch) Ahead = false;
            return new(0, PushReportsBranch
                ? " \trefs/heads/sample:refs/heads/sample\told..new\n"
                : "=\trefs/heads/other:refs/heads/other\t[up to date]\n", "");
        }

        public Task<IReadOnlyList<GameCardItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
            FailLibraryRead ? Task.FromException<IReadOnlyList<GameCardItem>>(new IOException("fixture read failed"))
                : Task.FromResult(Games);
        public AppSettings Get() => Settings;
        public Task AppendAsync(RepositorySyncHistoryItem entry, CancellationToken cancellationToken = default)
        {
            History.Add(entry);
            return Task.CompletedTask;
        }
        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Notifications.Add(title);
            return Task.CompletedTask;
        }
        public Task AppendErrorAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetState(RepositorySyncState state) { }
        public void Dispose() => Sync.Dispose();
        public Task<GameCardItem?> FindByIdAsync(string gameId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertAsync(GameCardItem game, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InsertWithSaveLinksAsync(GameCardItem game, IReadOnlyList<GameSaveLinkItem> links, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpsertWithSaveLinksAsync(GameCardItem game, IReadOnlyList<GameSaveLinkItem> links, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string gameId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AppendGitErrorAsync(string? repositoryPath, IReadOnlyList<string> arguments, GitCommandResult result,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MaintainLogFilesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LogViewerEntry>> GetLatestEntriesAsync(int limit, LogLevel? severity = null,
            string? keyword = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task OpenLatestErrorLogAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, IReadOnlyList<RepositorySyncHistoryItem>>> GetLatestByRepositoryIdsAsync(
            IReadOnlyCollection<string> repositoryIds, int limitPerRepository, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly List<PendingDelay> _delays = [];
        public override DateTimeOffset GetUtcNow() => _now;

        public Task Delay(TimeSpan wait, CancellationToken token)
        {
            var completion = new TaskCompletionSource();
            _delays.Add(new(_now + wait, completion, token));
            return completion.Task.WaitAsync(token);
        }

        public void Advance(TimeSpan amount)
        {
            var end = _now + amount;
            for (var count = 0; count < 50; count++)
            {
                _delays.RemoveAll(static delay => delay.Token.IsCancellationRequested);
                var next = _delays.Where(delay => delay.Due <= end).MinBy(static delay => delay.Due);
                if (next is null)
                {
                    _now = end;
                    return;
                }
                _delays.Remove(next);
                _now = next.Due;
                next.Completion.SetResult();
            }
            throw new InvalidOperationException("Fixture ran too many delays during one advance.");
        }

        private sealed record PendingDelay(DateTimeOffset Due, TaskCompletionSource Completion, CancellationToken Token);
    }
}
