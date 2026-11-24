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

public sealed class SyncOrchestratorTests
{
    // @test-value v2
    // kind = "invariant"
    // claim = "競合停止中のリポジトリは監視通知や一括同期で再実行されず、明示再開で同期できる"
    // oracle = { type = "contract", ref = "docs/design/maui-blazor-architecture.md §10 エラーハンドリング方針 / §12 実装ステータス（ErrorPaused）; docs/要件定義.md §8.4 競合・失敗時の扱い" }
    // fault = "競合直前に受けた再実行フラグや停止後の監視通知が停止を解除する"
    // observable = "公開状態ストアの停止状態、一括同期要求の受付有無、同期履歴、Git 呼び出し回数"
    // observation_boundary = "public-boundary"
    // scope = "sync scheduling and conflict recovery"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ConflictStaysPausedUntilExplicitResume()
    {
        const string repository = "fixture";
        var deps = new SyncDependencies();
        var states = new RepositoryStateStore();
        using var sync = new SyncOrchestrator(states, deps, deps, deps, deps, deps, deps, deps,
            NullLogger<SyncOrchestrator>.Instance);

        Assert.True(sync.TryQueueRepositorySyncNow(repository));
        Assert.Equal(RepositorySyncState.ErrorPaused, states.GetStateOrDefault(repository));
        Assert.Equal(RepositorySyncHistoryStatus.Paused, Assert.Single(deps.History).Status);

        deps.WatcherChanged(repository);
        Assert.False(sync.TryQueueRepositorySyncNow(repository));
        Assert.Equal(RepositorySyncState.ErrorPaused, states.GetStateOrDefault(repository));
        Assert.Equal(1, deps.FetchCount);

        deps.ShouldConflict = false;
        await sync.ResumeRepositorySyncAsync(repository);
        Assert.Equal(RepositorySyncState.Idle, states.GetStateOrDefault(repository));
        Assert.Equal(RepositorySyncHistoryStatus.Succeeded, deps.History.Last().Status);
        Assert.Equal(2, deps.FetchCount);
    }

    private sealed class SyncDependencies : IRepositoryWatcherService, IGitService, IAppSettingsService,
        INotificationService, ITrayService, ILogAccessService, IRepositorySyncHistoryStore
    {
        public bool ShouldConflict { get; set; } = true;
        public int FetchCount { get; private set; }
        public List<RepositorySyncHistoryItem> History { get; } = [];
        public event EventHandler<RepositoryChangedEventArgs>? RepositoryChanged;

        public void WatcherChanged(string repository) =>
            RepositoryChanged?.Invoke(this, new RepositoryChangedEventArgs(repository));

        public Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            var result = arguments[0] switch
            {
                "fetch" => Fetch(),
                "branch" => new(0, "test", ""),
                "config" => new(0, arguments[2].EndsWith(".remote", StringComparison.Ordinal) ? "origin" : "refs/heads/test", ""),
                "pull" when ShouldConflict => Conflict(repositoryPath),
                "pull" or "add" or "status" or "push" => new(0, "", ""),
                _ => throw new InvalidOperationException($"Unexpected Git command: {arguments[0]}")
            };
            return Task.FromResult(result);
        }

        private GitCommandResult Fetch()
        {
            FetchCount++;
            return new(0, "", "");
        }

        private GitCommandResult Conflict(string repository)
        {
            WatcherChanged(repository);
            return new(1, "", "CONFLICT");
        }

        public Task AppendAsync(RepositorySyncHistoryItem entry, CancellationToken cancellationToken = default)
        {
            History.Add(entry);
            return Task.CompletedTask;
        }

        public AppSettings Get() => AppSettings.Default;
        public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AppendErrorAsync(string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void SetState(RepositorySyncState state) { }

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
}
