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
    // claim = "枝切替通知だけでは0/0・競合停止・未登録リポジトリのいずれもGitを開始しない"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-branch-change-contract" }
    // fault = "枝切替通知を手動同期や停止解除として扱い、設定・現在の登録状態を無視する"
    // observable = "各条件での枝切替受付後のGitコマンド不在と停止状態の維持"
    // observation_boundary = "public-boundary"
    // scope = "branch change notification while cadence cannot run"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData("disabled")]
    [InlineData("paused")]
    [InlineData("unregistered")]
    public async Task BranchChangeNoticeDoesNotEnableCadenceOrResumeOrSyncAnUnregisteredRepository(string condition)
    {
        using var f = new Fixture(commit: condition == "disabled" ? 0 : 1, push: 0);
        if (condition == "paused")
        {
            f.ConflictedIndex = true;
            Assert.True(f.Sync.TryQueueRepositorySyncNow(f.Repository));
            Assert.Equal(RepositorySyncState.ErrorPaused, f.State);
        }
        else if (condition == "unregistered")
        {
            f.Games = [];
        }
        var before = f.Commands.Count;
        await f.Sync.NotifyRepositoryBranchChangedAsync(f.Repository).WaitAsync(TimeSpan.FromSeconds(5));
        f.Advance(seconds: 120);
        Assert.Equal(before, f.Commands.Count);
        Assert.Equal(condition == "paused" ? RepositorySyncState.ErrorPaused : RepositorySyncState.Idle, f.State);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "保留を見つけなかった正値照会の後に0/0にすると、次の変更は従来経路から始まる"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-independent-review CAD-CORE-02" }
    // fault = "最後の正値実行を理由に、保留がなくても新しい0/0設定で旧経路に戻らない"
    // observable = "正値照会で履歴なし、切替後はfetchから開始し引数なしpushで終了"
    // observation_boundary = "public-boundary"
    // scope = "zero cadence after a clean positive-cadence probe"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void CleanProbeDoesNotKeepCadenceAfterSettingsReturnToZero()
    {
        using var f = new Fixture(commit: 1, push: 1);
        f.Watch();
        Assert.Contains("status --porcelain", f.Commands);
        Assert.Empty(f.History);
        var before = f.Commands.Count;
        f.Settings = AppSettings.Default;
        f.Dirty = true;
        f.Change();
        f.Advance(seconds: 9);
        Assert.Equal(before, f.Commands.Count);
        f.Advance(seconds: 1);
        Assert.Equal("fetch", f.Commands[before]);
        Assert.Equal("push", f.Commands.Last());
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, Assert.Single(f.History).Status);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "0/0の監視予約・DB失敗後の再試行も、別名付替え後の旧物理先にはGitを開始しない"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-independent-review CAD-CORE-04" }
    // fault = "旧経路のデバウンスまたは再試行だけが現在の登録を照合しない"
    // observable = "付替えとDBエラーを挟んだ後の旧物理先へのGit呼出しなし"
    // observation_boundary = "public-boundary"
    // scope = "old queue identity with zero cadence"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroCadenceDoesNotRunOnTheOldPhysicalTarget(bool retry)
    {
        const string alias = "test-alias";
        var target = Path.Combine(Path.GetTempPath(), "cadence-repository");
        using var f = new Fixture(commit: 0, push: 0, resolveDirectoryPath: path => path == alias ? target : path);
        f.Games = [f.Game("aliased", alias)];
        f.Watch();
        f.Change();
        if (retry)
        {
            f.FailLibraryRead = true;
            f.Advance(seconds: 10);
            Assert.Equal(RepositorySyncHistoryStatus.Failed, Assert.Single(f.History).Status);
            f.FailLibraryRead = false;
        }
        target += "-new";
        f.Advance(seconds: 30);
        Assert.Empty(f.Commands);
        Assert.Equal(retry ? 1 : 0, f.History.Count);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "旧コミットの送信期限は、新しい書込のデバウンスを破らず、既存コミットだけを送信する"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-independent-review CAD-CORE-06" }
    // fault = "期限起床前またはDB照合・Git statusのawait中に届いた書込を未成熟のままステージ・autostashする"
    // observable = "送信期限ではpushのみ、後続の10秒満了で初めてcommit"
    // observation_boundary = "public-boundary"
    // scope = "outgoing deadline while a fresh change is debouncing"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData("before")]
    [InlineData("database")]
    [InlineData("status")]
    public void OutgoingDeadlineDoesNotBypassChangeDebounce(string changeTiming)
    {
        using var f = new Fixture(commit: 0, push: 1) { Ahead = true };
        f.Watch();
        f.Advance(seconds: 59);
        var checkpoint = f.Commands.Count;
        var gate = new TaskCompletionSource<IReadOnlyList<GameCardItem>>();
        var statusGate = new TaskCompletionSource<GitCommandResult>();
        if (changeTiming == "database")
        {
            var reads = f.LibraryReads;
            f.LibraryReadGate = gate;
            f.Advance(seconds: 1);
            Assert.Equal(reads + 1, f.LibraryReads);
            Assert.Equal(checkpoint, f.Commands.Count); // Due claimed and waiting on the DB; no status yet.
        }
        else if (changeTiming == "status")
        {
            f.StatusGate = statusGate;
            f.Advance(seconds: 1);
            Assert.Equal("status --porcelain", Assert.Single(f.Commands.Skip(checkpoint)));
        }
        f.Dirty = true;
        f.Change();
        if (changeTiming == "database")
            gate.SetResult(f.Games);
        else if (changeTiming == "status")
            statusGate.SetResult(new(0, " M save.dat\n", ""));
        else
            f.Advance(seconds: 1);
        Assert.Equal((0, 1), (f.Commits, f.Pushes));
        Assert.DoesNotContain(f.Commands.Skip(checkpoint), command =>
            command is "add -A" or "fetch" || command.StartsWith("pull ", StringComparison.Ordinal));
        Assert.True(f.Dirty);
        Assert.Empty(f.History);
        f.Advance(seconds: changeTiming == "before" ? 8 : 9);
        Assert.Equal(0, f.Commits);
        f.Advance(seconds: 1);
        Assert.Equal((1, 1), (f.Commits, f.Pushes));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "変更や監視再登録でコミット期限を延ばさず、以後の変更がなくてもHome離脱中に独立したpush期限で送信する"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract; game-sync-cadence-watch-lifetime" }
    // fault = "変更で期限を更新する、コミットをpush期限まで待つ、監視解除で保持した期限を捨てる、または待機を成功にする"
    // observable = "仮想時刻ごとのcommit・push呼出し、追加commit後も最初の送信期限、送信前の履歴・通知なし、送信後の成功履歴"
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
        f.Advance(seconds: 1);
        f.Dirty = true;
        f.Change();
        f.Advance(seconds: 120);
        Assert.Equal((2, 0), (f.Commits, f.Pushes));
        f.Unwatch();
        f.Games = [f.Game("other", f.Repository + Path.DirectorySeparatorChar)];
        f.Advance(seconds: 58);
        Assert.Equal(0, f.Pushes);
        f.Advance(seconds: 1);

        Assert.Equal(2, f.Commits);
        Assert.Equal(1, f.Pushes);
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, Assert.Single(f.History).Status);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "作業ツリー変更が継続してデバウンス完了を待てなくても、最初の検知からのコミット期限は維持される"
    // oracle = { type = "contract", ref = "Session Graph game-git-cadence-settings-contract" }
    // fault = "変更検知の継続で最初のコミット期限を超過する"
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
        if (outgoing && !dirty)
            Assert.Equal(RepositorySyncHistoryStatus.Succeeded, Assert.Single(f.History).Status);
        else
            Assert.Empty(f.History);
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
    // observable = "照合失敗時の失敗履歴、DB復帰後の再照合で登録ありならcommit、削除済みならGit呼出しなし"
    // observation_boundary = "public-boundary"
    // scope = "registration authority and retry at deadline"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeadlineRechecksRegistrationAndDoesNotTreatDbFailureAsRemoval(bool readFails, bool removedDuringRetry)
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
            if (removedDuringRetry) f.Games = [];
            var reads = f.LibraryReads;
            f.Advance(seconds: 5);
            Assert.Equal(reads + 1, f.LibraryReads);
            if (removedDuringRetry)
            {
                Assert.Equal(before, f.Commands.Count);
                Assert.Single(f.History);
            }
            else
            {
                Assert.Equal(1, f.Commits);
                Assert.Equal(RepositorySyncHistoryStatus.Succeeded, f.History.Last().Status);
            }
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
    // fault = "watcher、DB、同期受付で異なる文字列を同一物理先として扱えない"
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
    // claim = "手動と明示再開は期限を待たず、pull/自動stash復元の競合で停止したら監視・期限・通常の即時受付で解除しない"
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
    // claim = "送信元枝または宛先を替えた次の確認では、前の送信期限や再試行を流用しない"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-independent-review CAD-CORE-03" }
    // fault = "旧対象の期限・backoffで変更後の宛先へ送信する"
    // observable = "旧失敗履歴、新対象が旧再試行時刻では未送信、新対象の期限でその宛先へ送信"
    // observation_boundary = "public-boundary"
    // scope = "outgoing deadlines and retry identity by branch and destination"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingBranchOrDestinationDoesNotInheritTheFailedPushDeadline(bool changeSource)
    {
        using var f = new Fixture(commit: 0, push: 1, retry: 30) { Ahead = true, PushReportsBranch = false };
        f.Watch();
        f.Advance(seconds: 60);
        Assert.Equal(RepositorySyncHistoryStatus.Failed, Assert.Single(f.History).Status);
        f.Advance(seconds: 1);
        if (changeSource) f.Branch = "another";
        else f.Destination = "different-destination";
        f.Oldest = f.Clock.GetUtcNow().AddDays(-1);
        f.PushReportsBranch = true;
        if (changeSource)
            await f.Sync.NotifyRepositoryBranchChangedAsync(f.Repository).WaitAsync(TimeSpan.FromSeconds(5));
        else
        {
            f.Change();
            f.Advance(seconds: 10);
        }
        f.Advance(seconds: 59);
        Assert.Equal(1, f.Pushes);
        Assert.Single(f.History);
        f.Advance(seconds: 1);
        Assert.Equal(2, f.Pushes);
        Assert.EndsWith($":refs/heads/{f.Destination}", f.Commands.Last());
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, f.History.Last().Status);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "枝と宛先refが同じならremote名の変更で期限・再試行を保ち、実URLの変更で新しい期限になる"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-independent-review CAD-CORE-03" }
    // fault = "remote名の別名で再試行を初期化する、または同じremote名の別実URLで旧期限を流用する"
    // observable = "仮想時計の旧backoff満了/新期限での送信回数と実URL、成功履歴"
    // observation_boundary = "public-boundary"
    // scope = "old endpoint retry versus a newly selected endpoint"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteAliasKeepsTheRetryButNewUrlStartsANewDeadline(bool changeUrl)
    {
        using var f = new Fixture(commit: 0, push: 1, retry: 30) { Ahead = true, PushReportsBranch = false };
        f.Watch();
        f.Advance(seconds: 60);
        Assert.Equal(RepositorySyncHistoryStatus.Failed, Assert.Single(f.History).Status);
        f.Advance(seconds: 1);
        if (changeUrl) f.PushUrl = "new-fixture-remote";
        else f.PushRemote = "origin-alias";
        f.PushReportsBranch = true;
        f.Change();

        f.Advance(seconds: 28); // t=89, first retry is due at t=90.
        Assert.Equal(1, f.Pushes);
        f.Advance(seconds: 1);
        Assert.Equal(changeUrl ? 1 : 2, f.Pushes);
        if (changeUrl)
        {
            f.Advance(seconds: 40); // The new URL was first seen at t=71.
            Assert.Equal(1, f.Pushes);
            f.Advance(seconds: 1);
        }
        Assert.Equal(new[] { "fixture-remote", f.PushUrl }, f.PushEndpoints);
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, f.History.Last().Status);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "旧枝の期限を過ぎても非選択中は送信せず、戻ったときはGitに残る未送信を再検出する"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-independent-review CAD-CORE-03" }
    // fault = "非選択の枝を送信する、または再選択時に古いコミットを検出せず期限を初めから待つ"
    // observable = "追跡情報なし、ファイル通知なしでの枝切替受付と復帰前後のpush回数、宛先、成功履歴"
    // observation_boundary = "public-boundary"
    // scope = "reselecting a branch with previously unsent commits"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(20)]
    [InlineData(75)]
    public async Task ReselectingABranchChecksItsPendingWorkWithoutFileEvents(int timeAway)
    {
        using var f = new Fixture(commit: 0, push: 1) { Ahead = true, TargetKnown = false };
        var originalCommit = f.Oldest;
        f.Watch();
        f.Branch = f.Destination = "another";
        f.Ahead = false;
        f.TargetKnown = true;
        await f.Sync.NotifyRepositoryBranchChangedAsync(f.Repository).WaitAsync(TimeSpan.FromSeconds(5));
        f.Advance(seconds: timeAway);
        Assert.Equal(0, f.Pushes);
        Assert.Empty(f.History);

        f.Branch = f.Destination = "sample";
        f.Ahead = true;
        f.Oldest = originalCommit;
        await f.Sync.NotifyRepositoryBranchChangedAsync(f.Repository).WaitAsync(TimeSpan.FromSeconds(5));
        if (timeAway < 60)
        {
            f.Advance(seconds: 59 - timeAway);
            Assert.Equal(0, f.Pushes);
            f.Advance(seconds: 1);
        }
        Assert.Equal(1, f.Pushes);
        Assert.EndsWith(":refs/heads/sample", f.Commands.Last());
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, Assert.Single(f.History).Status);
        Assert.Equal(2, f.Commands.Count(command => command.StartsWith("ls-remote ", StringComparison.Ordinal)));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "進行中に受けた再開要求は、その後発生した新しい競合停止を解除しない"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-independent-review CAD-CORE-05" }
    // fault = "稼働中のResumeを停止解除として予約し、競合後に別の実行を開始する"
    // observable = "fetchを止めた時の受付完了、後発競合の停止、受付済みResumeによる2度目のfetchなし"
    // observation_boundary = "public-boundary"
    // scope = "resume accepted before synchronization was paused"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ResumeAcceptedBeforeAConflictCannotReleaseTheNewPause()
    {
        var gate = new TaskCompletionSource<GitCommandResult>();
        using var f = new Fixture(commit: 1, push: 1) { Dirty = true, Conflict = true, FetchGate = gate };
        Assert.True(f.Sync.TryQueueRepositorySyncNow(f.Repository));
        Assert.Equal("fetch", f.Commands.Last());
        try
        {
            await f.Sync.ResumeRepositorySyncAsync(f.Repository).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.SetResult(new(0, "", ""));
        }
        Assert.Equal(RepositorySyncState.ErrorPaused, f.State);
        Assert.Equal(RepositorySyncHistoryStatus.Paused, Assert.Single(f.History).Status);
        Assert.Single(f.Commands, command => command == "fetch");
        Assert.Equal((0, 0), (f.Commits, f.Pushes));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ResumeRepositorySyncAsyncのtokenがfetch中に取り消されると、後続Gitや失敗再試行を開始しない"
    // oracle = { type = "contract", ref = "Session Graph game-sync-cadence-core-worker; src/GameLauncherWithGit/Infrastructure/Abstractions/IGitService.cs" }
    // fault = "Gitの取消を失敗または成功として扱い後続コマンドや自動再試行を実行する"
    // observable = "取消後のGit呼出し、状態と同期履歴"
    // observation_boundary = "public-boundary"
    // scope = "explicit resume caller cancellation at fetch"
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
        Assert.Equal("fetch", f.Commands.Last());
        Assert.Equal(RepositorySyncState.Idle, f.State);
        var before = f.Commands.Count;
        f.Advance(seconds: 600);
        Assert.Equal(before, f.Commands.Count);
        Assert.Equal((0, 0), (f.Commits, f.Pushes));
        Assert.Empty(f.History);
    }

    internal sealed class Fixture : IDisposable, IRepositoryWatcherService, IGameLibraryStore, IGitService,
        IAppSettingsService, INotificationService, ITrayService, ILogAccessService, IRepositorySyncHistoryStore
    {
        private string Head => (Commits + 1).ToString("x40", CultureInfo.InvariantCulture);
        private static readonly string RemoteTip = new('a', 40);
        public string Branch { get; set; } = "sample";
        public string Destination { get; set; } = "sample";
        public string PushRemote { get; set; } = "origin";
        public string PushUrl { get; set; } = "fixture-remote";
        private readonly RepositoryStateStore _states = new();
        public string Repository { get; }
        public ManualClock Clock { get; } = new();
        public SyncOrchestrator Sync { get; }
        public List<string> Commands { get; } = [];
        public List<string> PushEndpoints { get; } = [];
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
        public int LibraryReads { get; private set; }
        public CancellationTokenSource? CancelOnFetch { get; set; }
        public TaskCompletionSource<GitCommandResult>? FetchGate { get; set; }
        public TaskCompletionSource<GitCommandResult>? StatusGate { get; set; }
        public TaskCompletionSource<IReadOnlyList<GameCardItem>>? LibraryReadGate { get; set; }
        public TaskCompletionSource<RepositorySyncHistoryItem> FirstHistory { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> FirstNotification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Commits { get; private set; }
        public int Pushes { get; private set; }
        public RepositorySyncState State => _states.GetStateOrDefault(Repository);
        private bool _watching;

        public Fixture(int commit, int push, int retry = 5, Func<string, string>? resolveDirectoryPath = null,
            string? repository = null, IGitService? git = null, IRepositoryWatcherService? watcher = null)
        {
            Repository = repository ?? Path.Combine(Path.GetTempPath(), "cadence-repository");
            Settings = AppSettings.Default with
            {
                AutoCommitIntervalMinutes = commit, AutoPushIntervalMinutes = push, SyncRetryInitialSeconds = retry
            };
            Games = [Game("fixture", Repository)];
            Oldest = Clock.GetUtcNow();
            var logger = NullLogger<SyncOrchestrator>.Instance;
            Sync = git is null
                ? new SyncOrchestrator(_states, watcher ?? this, this, this, this, this, this, this,
                    logger, Clock.Delay, Clock, this, resolveDirectoryPath)
                : new SyncOrchestrator(_states, watcher ?? this, git, this, this, this, this, this, this, logger);
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
            if (command == "fetch" && FetchGate is { } gate)
            {
                FetchGate = null;
                return gate.Task;
            }
            if (command == "status --porcelain" && StatusGate is { } statusGate)
            {
                StatusGate = null;
                return statusGate.Task;
            }
            GitCommandResult result = command switch
            {
                "status --porcelain" => new(0, ConflictedIndex ? "UU save.dat\n" : Dirty ? " M save.dat\n" : "", ""),
                var c when c == $"rev-parse --verify --quiet refs/heads/{Branch}" => new(0, Head, ""),
                "symbolic-ref --quiet HEAD" => new(0, $"refs/heads/{Branch}\n", ""),
                var c when c == $"config --null --get-all branch.{Branch}.remote" => new(0, "origin\0", ""),
                var c when c == $"config --null --get-all branch.{Branch}.pushRemote" && PushRemote != "origin" =>
                    new(0, PushRemote + "\0", ""),
                var c when c == $"config --null --get-all remote.{PushRemote}.push" =>
                    new(0, $"refs/heads/{Branch}:refs/heads/{Destination}\0", ""),
                var c when c.StartsWith("config --null ", StringComparison.Ordinal) => new(1, "", ""),
                var c when c == $"check-ref-format refs/heads/{Destination}" => new(0, "", ""),
                var c when c.StartsWith("rev-parse --verify --quiet ", StringComparison.Ordinal) && c.EndsWith("^{commit}", StringComparison.Ordinal)
                    => new(0, "", ""),
                var c when c.StartsWith("log --format=%ct ", StringComparison.Ordinal) =>
                    new(0, Ahead ? Oldest.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + "\n" : "", ""),
                var c when c.StartsWith("rev-list --count ", StringComparison.Ordinal) => new(0, Ahead ? "1\n" : "0\n", ""),
                "branch --show-current" => new(0, Branch, ""),
                var c when c == $"config --get branch.{Branch}.remote" => new(0, "origin", ""),
                var c when c == $"config --get branch.{Branch}.merge" => new(0, $"refs/heads/{Branch}", ""),
                var c when c == $"pull --rebase --autostash origin {Branch}" && Conflict => new(1, "CONFLICT", ""),
                var c when c == $"pull --rebase --autostash origin {Branch}" && ConflictAfterPull => ApplyConflictingAutostash(),
                "fetch" when CancelOnFetch is { } cancel => Cancel(cancel),
                "fetch" or "add -A" => new(0, "", ""),
                var c when c == $"pull --rebase --autostash origin {Branch}" => new(0, "", ""),
                "commit" => Commit(),
                "push" => Push(),
                _ => throw new NotSupportedException(command)
            };
            return Task.FromResult(result);
        }

        Task<GitCommandResult> IGitService.ReadCadencePushUrlsAsync(string repositoryPath, string remote, CancellationToken token)
        {
            Assert.Equal(Repository, repositoryPath);
            Assert.Equal(PushRemote, remote);
            return Task.FromResult(new GitCommandResult(0, PushUrl + "\n", ""));
        }

        Task<GitCommandResult> IGitService.RunCadenceEndpointAsync(string repositoryPath, string resolvedUrl,
            string destinationRef, string? commit, CancellationToken token)
        {
            Assert.Equal(Repository, repositoryPath);
            Assert.Equal(PushUrl, resolvedUrl);
            Assert.Equal($"refs/heads/{Destination}", destinationRef);
            if (commit is null)
            {
                Commands.Add($"ls-remote {destinationRef}");
                return Task.FromResult(TargetKnown
                    ? new GitCommandResult(0, RemoteTip, "") : new GitCommandResult(1, "", "", "Remote unavailable"));
            }
            Assert.Equal(Head, commit);
            Commands.Add($"push {commit}:{destinationRef}");
            PushEndpoints.Add(resolvedUrl);
            Push();
            return Task.FromResult(PushReportsBranch
                ? new GitCommandResult(0, "", "") : new GitCommandResult(1, "", "", "Unconfirmed"));
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
                ? $" \t{Head}:refs/heads/{Destination}\told..new\n"
                : "=\trefs/heads/other:refs/heads/other\t[up to date]\n", "");
        }

        public Task<IReadOnlyList<GameCardItem>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            LibraryReads++;
            if (LibraryReadGate is { } gate)
            {
                LibraryReadGate = null;
                return gate.Task;
            }
            return FailLibraryRead ? Task.FromException<IReadOnlyList<GameCardItem>>(new IOException("fixture read failed"))
                : Task.FromResult(Games);
        }
        public AppSettings Get() => Settings;
        public Task AppendAsync(RepositorySyncHistoryItem entry, CancellationToken cancellationToken = default)
        {
            History.Add(entry);
            FirstHistory.TrySetResult(entry);
            return Task.CompletedTask;
        }
        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Notifications.Add(title);
            FirstNotification.TrySetResult($"{title}: {message}");
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

    internal sealed class ManualClock : TimeProvider
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
