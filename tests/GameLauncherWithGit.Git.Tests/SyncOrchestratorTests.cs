using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Domain.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;
using Xunit;

namespace GameLauncherWithGit.Git.Tests;

public sealed class SyncOrchestratorTests
{
    private const string Repository = "fixture";

    // @test-value v2
    // kind = "invariant"
    // claim = "監視と再実行は通常時に働き、競合停止後は明示再開まで次の同期を実行しない"
    // oracle = { type = "contract", ref = "docs/要件定義.md §8.2 デバウンス / §8.4 競合・失敗時の扱い; src/GameLauncherWithGit/Application/Abstractions/ISyncOrchestrator.cs; Session Graph app-wide-sync-queue-vs-completion" }
    // fault = "監視や通常の再実行が無効、受付を終了として扱う、または競合で止まった同期を監視や一括操作で解除する"
    // observable = "制御された待機・Git呼び出し、公開状態、同期履歴、一括受付結果"
    // observation_boundary = "public-boundary"
    // scope = "sync scheduling and conflict recovery"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ConflictStaysPausedUntilExplicitResume()
    {
        var deps = new SyncDependencies();
        var states = new RepositoryStateStore();
        using var sync = CreateSync(deps, states);

        deps.WatcherChanged(Repository);
        var initialWait = await deps.NextWaitAsync();
        Assert.Equal(RepositorySyncState.Debouncing, states.GetStateOrDefault(Repository));
        Assert.Equal(0, deps.FetchCount);
        initialWait.Release();

        var initialFetch = await deps.NextFetchAsync();
        Assert.Equal(RepositorySyncState.Syncing, states.GetStateOrDefault(Repository));
        Assert.Equal(0, deps.HistoryCount);
        deps.WatcherChanged(Repository);
        initialFetch.Release();
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, (await deps.NextHistoryAsync()).Status);

        var rerunWait = await deps.NextWaitAsync();
        rerunWait.Release();
        var rerunFetch = await deps.NextFetchAsync();
        deps.WatcherChanged(Repository); // A queued rerun must not clear a pause decided by the current run.
        deps.ShouldConflict = true;
        rerunFetch.Release();
        Assert.Equal(RepositorySyncHistoryStatus.Paused, (await deps.NextHistoryAsync()).Status);

        deps.WatcherChanged(Repository);
        Assert.False(sync.TryQueueRepositorySyncNow(Repository));
        Assert.Equal(RepositorySyncState.ErrorPaused, states.GetStateOrDefault(Repository));
        Assert.Equal(2, deps.FetchCount);

        deps.ShouldConflict = false;
        await sync.ResumeRepositorySyncAsync(Repository);
        var resumedFetch = await deps.NextFetchAsync();
        Assert.Equal(RepositorySyncState.Syncing, states.GetStateOrDefault(Repository));
        Assert.Equal(2, deps.HistoryCount);
        resumedFetch.Release();
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, (await deps.NextHistoryAsync()).Status);
        Assert.Equal(3, deps.FetchCount);
        Assert.Equal(2, deps.WaitCount);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "即時受付とGit開始の間に監視が通知しても、即時予約は通常の待機に降格しない"
    // oracle = { type = "contract", ref = "Session Graph app-wide-core-review-required-fixes CORE-03; src/GameLauncherWithGit/Application/Abstractions/ISyncOrchestrator.cs: TryQueueRepositorySyncNow" }
    // fault = "監視がまだGitを始めていない即時予約のトークンを置き換え、デバウンスに戻す"
    // observable = "監視通知直後の公開状態、待機回数、Git開始、完了前の履歴なし"
    // observation_boundary = "public-boundary"
    // scope = "immediate sync reservation priority"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task WatcherCannotDemoteImmediateReservationBeforeGitStarts()
    {
        var deps = new SyncDependencies();
        var states = new RepositoryStateStore();
        using var sync = CreateSync(deps, states);
        deps.OnTrayState = state =>
        {
            if (state != RepositorySyncState.Syncing) return;
            deps.OnTrayState = null;
            Assert.Equal(0, deps.FetchCount);
            deps.WatcherChanged(Repository);
            Assert.Equal(RepositorySyncState.Syncing, states.GetStateOrDefault(Repository));
        };

        Assert.True(sync.TryQueueRepositorySyncNow(Repository));
        var fetch = await deps.NextFetchAsync();
        Assert.Equal(0, deps.WaitCount);
        Assert.Equal(0, deps.HistoryCount);
        fetch.Release();
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, (await deps.NextHistoryAsync()).Status);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "置換された待機は現在の状態を書き換えず、現在の待機を取り消せば待機状態が残らない"
    // oracle = { type = "contract", ref = "Session Graph app-wide-core-review-required-fixes CORE-03; src/GameLauncherWithGit/Application/Abstractions/ISyncOrchestrator.cs: QueueRepositorySyncAsync" }
    // fault = "古い待機が新しい待機の状態を初期化する、または現在の待機を取り消しても状態がDebouncingに残る"
    // observable = "待機代替に渡した各トークン、公開状態ストア、Gitと履歴の呼び出し数"
    // observation_boundary = "public-boundary"
    // scope = "ownership and cancellation of pending debounce"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task CancelingPendingDebounceDoesNotLeaveStaleStateOrStartGit()
    {
        var deps = new SyncDependencies { LetFirstWaitFinishAfterCancellation = true };
        var states = new RepositoryStateStore();
        using var sync = CreateSync(deps, states);
        using var cancel = new CancellationTokenSource();
        await sync.QueueRepositorySyncAsync(Repository);
        var previous = await deps.NextWaitAsync();
        await sync.QueueRepositorySyncAsync(Repository, cancel.Token);
        var current = await deps.NextWaitAsync();

        Assert.True(previous.Token.IsCancellationRequested);
        Assert.False(current.Token.IsCancellationRequested);
        // A superseded wait can finish late; it must neither run Git nor change the live reservation's state.
        previous.Release();
        Assert.Equal(RepositorySyncState.Debouncing, states.GetStateOrDefault(Repository));
        var returnedToIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        deps.OnTrayState = state =>
        {
            if (state == RepositorySyncState.Idle) returnedToIdle.TrySetResult();
        };
        cancel.Cancel();
        await returnedToIdle.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RepositorySyncState.Idle, states.GetStateOrDefault(Repository));
        Assert.Equal(0, deps.FetchCount);
        Assert.Equal(0, deps.HistoryCount);
    }

    private static SyncOrchestrator CreateSync(SyncDependencies deps, RepositoryStateStore states) =>
        new(states, deps, deps, deps, deps, deps, deps, deps, NullLogger<SyncOrchestrator>.Instance, deps.DelayAsync);

    private sealed class SyncDependencies : IRepositoryWatcherService, IGitService, IAppSettingsService,
        INotificationService, ITrayService, ILogAccessService, IRepositorySyncHistoryStore
    {
        private readonly Channel<Gate> _fetches = Channel.CreateUnbounded<Gate>();
        private readonly Channel<Gate> _waits = Channel.CreateUnbounded<Gate>();
        private readonly Channel<RepositorySyncHistoryItem> _history = Channel.CreateUnbounded<RepositorySyncHistoryItem>();
        private int _fetchCount, _waitCount, _historyCount;
        public bool ShouldConflict { get; set; }
        public int FetchCount => Volatile.Read(ref _fetchCount);
        public int WaitCount => Volatile.Read(ref _waitCount);
        public int HistoryCount => Volatile.Read(ref _historyCount);
        public bool LetFirstWaitFinishAfterCancellation { get; init; }
        public Action<RepositorySyncState>? OnTrayState { get; set; }
        public event EventHandler<RepositoryChangedEventArgs>? RepositoryChanged;

        public void WatcherChanged(string repository) =>
            RepositoryChanged?.Invoke(this, new RepositoryChangedEventArgs(repository));

        public async Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            if (arguments[0] == "fetch")
            {
                Interlocked.Increment(ref _fetchCount);
                var gate = new Gate(cancellationToken);
                _fetches.Writer.TryWrite(gate);
                await gate.Task.WaitAsync(cancellationToken);
                return new(0, "", "");
            }

            return arguments[0] switch
            {
                "branch" => new(0, "test", ""),
                "config" => new(0, arguments[2].EndsWith(".remote", StringComparison.Ordinal) ? "origin" : "refs/heads/test", ""),
                "pull" when ShouldConflict => new(1, "", "CONFLICT"),
                "pull" or "add" or "status" or "push" => new(0, "", ""),
                _ => throw new InvalidOperationException($"Unexpected Git command: {arguments[0]}")
            };
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var waitNumber = Interlocked.Increment(ref _waitCount);
            var gate = new Gate(cancellationToken);
            _waits.Writer.TryWrite(gate);
            return waitNumber == 1 && LetFirstWaitFinishAfterCancellation ? gate.Task : gate.Task.WaitAsync(cancellationToken);
        }

        public Task<Gate> NextFetchAsync() => ReadAsync(_fetches);
        public Task<Gate> NextWaitAsync() => ReadAsync(_waits);
        public Task<RepositorySyncHistoryItem> NextHistoryAsync() => ReadAsync(_history);

        public Task AppendAsync(RepositorySyncHistoryItem entry, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _historyCount);
            _history.Writer.TryWrite(entry);
            return Task.CompletedTask;
        }

        public AppSettings Get() => AppSettings.Default;
        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendErrorAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetState(RepositorySyncState state) => OnTrayState?.Invoke(state);

        private static async Task<T> ReadAsync<T>(Channel<T> channel)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await channel.Reader.ReadAsync(timeout.Token);
        }

        public void Register(string repositoryId, string watchPath) => throw new NotSupportedException();
        public void Unregister(string repositoryId) => throw new NotSupportedException();
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

    private sealed class Gate(CancellationToken token)
    {
        private readonly TaskCompletionSource _completion = new();
        public CancellationToken Token { get; } = token;
        public Task Task => _completion.Task;
        public void Release() => _completion.TrySetResult();
    }
}
