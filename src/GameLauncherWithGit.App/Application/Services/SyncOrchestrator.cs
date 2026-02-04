using System.Collections.Concurrent;
using GameLauncherWithGit.App.Application.Abstractions;
using GameLauncherWithGit.App.Application.Models;
using GameLauncherWithGit.App.Infrastructure.Abstractions;
using GameLauncherWithGit.App.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.App.Application.Services;

public sealed class SyncOrchestrator : ISyncOrchestrator, IDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(3),
        TimeSpan.FromMinutes(5),
    };

    private readonly ConcurrentDictionary<string, RepositoryRuntime> _repositories = new();
    private readonly IGitService _gitService;
    private readonly IRepositoryWatcherService _watcherService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        IGitService gitService,
        IRepositoryWatcherService watcherService,
        INotificationService notificationService,
        ILogger<SyncOrchestrator> logger)
    {
        _gitService = gitService;
        _watcherService = watcherService;
        _notificationService = notificationService;
        _logger = logger;

        _watcherService.RepositoryChanged += OnRepositoryChanged;
    }

    public void RegisterRepository(RepositorySyncDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.RepositoryId))
        {
            throw new ArgumentException("リポジトリIDは必須です。", nameof(definition));
        }

        if (!Directory.Exists(definition.RepositoryPath))
        {
            throw new DirectoryNotFoundException($"リポジトリが見つかりません: {definition.RepositoryPath}");
        }

        if (!Directory.Exists(definition.WatchPath))
        {
            throw new DirectoryNotFoundException($"監視対象ディレクトリが見つかりません: {definition.WatchPath}");
        }

        RepositoryRuntime runtime = _repositories.AddOrUpdate(
            definition.RepositoryId,
            _ => new RepositoryRuntime(definition),
            (_, existing) =>
            {
                existing.Definition = definition;
                existing.IsPaused = false;
                return existing;
            });

        _watcherService.StartWatch(runtime.Definition.RepositoryId, runtime.Definition.WatchPath, runtime.Definition.DebounceSeconds);

        _logger.LogInformation(
            "同期対象を登録: repositoryId={RepositoryId}, repoPath={RepositoryPath}, watchPath={WatchPath}",
            runtime.Definition.RepositoryId,
            runtime.Definition.RepositoryPath,
            runtime.Definition.WatchPath);
    }

    public void UnregisterRepository(string repositoryId)
    {
        _watcherService.StopWatch(repositoryId);
        _repositories.TryRemove(repositoryId, out _);
        _logger.LogInformation("同期対象を解除: repositoryId={RepositoryId}", repositoryId);
    }

    public void RequestSync(string repositoryId, string reason)
    {
        if (!_repositories.TryGetValue(repositoryId, out RepositoryRuntime? runtime))
        {
            _logger.LogWarning("未登録リポジトリの同期要求を無視: repositoryId={RepositoryId}", repositoryId);
            return;
        }

        if (runtime.IsPaused)
        {
            _logger.LogWarning("停止中のため同期要求を無視: repositoryId={RepositoryId}, reason={Reason}", repositoryId, reason);
            return;
        }

        bool shouldStartWorker = false;

        lock (runtime.SyncRoot)
        {
            runtime.Pending = true;

            if (!runtime.WorkerRunning)
            {
                runtime.WorkerRunning = true;
                shouldStartWorker = true;
            }
        }

        if (shouldStartWorker)
        {
            _ = Task.Run(() => ProcessQueueAsync(runtime, CancellationToken.None));
        }
    }

    public async Task RequestImmediateSyncAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        RequestSync(repositoryId, "manual");

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_repositories.TryGetValue(repositoryId, out RepositoryRuntime? runtime))
            {
                return;
            }

            bool isCompleted;
            lock (runtime.SyncRoot)
            {
                isCompleted = !runtime.WorkerRunning && !runtime.Pending;
            }

            if (isCompleted)
            {
                return;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _watcherService.RepositoryChanged -= OnRepositoryChanged;
    }

    private async Task ProcessQueueAsync(RepositoryRuntime runtime, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (runtime.IsPaused)
            {
                SetWorkerStopped(runtime);
                return;
            }

            bool shouldRun;
            lock (runtime.SyncRoot)
            {
                shouldRun = runtime.Pending;
                runtime.Pending = false;
            }

            if (!shouldRun)
            {
                SetWorkerStopped(runtime);
                return;
            }

            SyncResult result = await ExecuteSyncCycleAsync(runtime, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                await NotifyRecoveredIfNeededAsync(runtime, cancellationToken).ConfigureAwait(false);
                runtime.RetryCount = 0;
                continue;
            }

            if (result.FailureKind == SyncFailureKind.Conflict)
            {
                runtime.IsPaused = true;
                await _notificationService.NotifyErrorAsync(
                    "自動同期を停止しました",
                    $"リポジトリ {runtime.Definition.RepositoryId} で競合が発生しました。手動解決後に再開してください。",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (result.IsTransientFailure)
            {
                runtime.RetryCount++;
                TimeSpan delay = GetRetryDelay(runtime.RetryCount);

                if (!runtime.HasNotifiedTransientFailure)
                {
                    await _notificationService.NotifyErrorAsync(
                        "同期に失敗しました",
                        $"リポジトリ {runtime.Definition.RepositoryId} は一時的なエラーです。復旧まで自動再試行します。",
                        cancellationToken).ConfigureAwait(false);
                    runtime.HasNotifiedTransientFailure = true;
                }

                _logger.LogWarning(
                    "一時エラーのため再試行待機: repositoryId={RepositoryId}, delay={Delay}, reason={Reason}",
                    runtime.Definition.RepositoryId,
                    delay,
                    result.Message);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                RequestSync(runtime.Definition.RepositoryId, "retry");
                continue;
            }

            await _notificationService.NotifyErrorAsync(
                "同期に失敗しました",
                $"リポジトリ {runtime.Definition.RepositoryId} の同期に失敗しました。ログを確認してください。",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SyncResult> ExecuteSyncCycleAsync(RepositoryRuntime runtime, CancellationToken cancellationToken)
    {
        string repositoryPath = runtime.Definition.RepositoryPath;

        SyncResult fetch = await ExecuteStepAsync(repositoryPath, "fetch", cancellationToken).ConfigureAwait(false);
        if (!fetch.IsSuccess)
        {
            return fetch;
        }

        SyncResult pull = await ExecuteStepAsync(repositoryPath, "pull --rebase", cancellationToken).ConfigureAwait(false);
        if (!pull.IsSuccess)
        {
            return pull;
        }

        SyncResult add = await ExecuteStepAsync(repositoryPath, "add -A", cancellationToken).ConfigureAwait(false);
        if (!add.IsSuccess)
        {
            return add;
        }

        GitCommandResult statusResult = await _gitService.ExecuteAsync(repositoryPath, "status --porcelain", cancellationToken).ConfigureAwait(false);
        LogCommandResult(repositoryPath, "status --porcelain", statusResult);

        if (!statusResult.IsSuccess)
        {
            return CreateFailure("status --porcelain", statusResult);
        }

        if (!string.IsNullOrWhiteSpace(statusResult.StandardOutput))
        {
            string message = $"auto: save sync {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            SyncResult commit = await ExecuteStepAsync(repositoryPath, $"commit -m \"{message}\"", cancellationToken).ConfigureAwait(false);
            if (!commit.IsSuccess)
            {
                return commit;
            }
        }

        SyncResult push = await ExecuteStepAsync(repositoryPath, "push", cancellationToken).ConfigureAwait(false);
        if (!push.IsSuccess)
        {
            return push;
        }

        _logger.LogInformation("同期成功: repositoryId={RepositoryId}", runtime.Definition.RepositoryId);
        return SyncResult.Success();
    }

    private async Task<SyncResult> ExecuteStepAsync(string repositoryPath, string args, CancellationToken cancellationToken)
    {
        GitCommandResult result = await _gitService.ExecuteAsync(repositoryPath, args, cancellationToken).ConfigureAwait(false);
        LogCommandResult(repositoryPath, args, result);

        if (result.IsSuccess)
        {
            return SyncResult.Success();
        }

        return CreateFailure(args, result);
    }

    private static SyncResult CreateFailure(string args, GitCommandResult result)
    {
        SyncFailureKind kind = ClassifyFailure(result);

        return SyncResult.Failure(
            $"git {args}",
            kind,
            result,
            $"git {args} の実行に失敗しました。exitCode={result.ExitCode}");
    }

    private static SyncFailureKind ClassifyFailure(GitCommandResult result)
    {
        string output = $"{result.StandardOutput}\n{result.StandardError}";

        if (ContainsAny(output, "conflict", "could not apply", "resolve all conflicts", "merge conflict"))
        {
            return SyncFailureKind.Conflict;
        }

        if (ContainsAny(output, "authentication failed", "fatal: could not read username", "permission denied (publickey)"))
        {
            return SyncFailureKind.Authentication;
        }

        if (ContainsAny(output, "could not resolve host", "failed to connect", "timed out", "network is unreachable"))
        {
            return SyncFailureKind.Network;
        }

        if (ContainsAny(output, "access denied", "permission denied", "not permitted"))
        {
            return SyncFailureKind.Permission;
        }

        return SyncFailureKind.Unknown;
    }

    private static bool ContainsAny(string source, params string[] patterns)
    {
        foreach (string pattern in patterns)
        {
            if (source.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan GetRetryDelay(int retryCount)
    {
        int index = Math.Clamp(retryCount - 1, 0, RetryDelays.Length - 1);
        return RetryDelays[index];
    }

    private async Task NotifyRecoveredIfNeededAsync(RepositoryRuntime runtime, CancellationToken cancellationToken)
    {
        if (!runtime.HasNotifiedTransientFailure)
        {
            return;
        }

        runtime.HasNotifiedTransientFailure = false;

        await _notificationService.NotifyInfoAsync(
            "同期が復旧しました",
            $"リポジトリ {runtime.Definition.RepositoryId} の同期が再開されました。",
            cancellationToken).ConfigureAwait(false);
    }

    private void OnRepositoryChanged(object? sender, RepositoryChangedEventArgs e)
    {
        RequestSync(e.RepositoryId, "watcher");
    }

    private void SetWorkerStopped(RepositoryRuntime runtime)
    {
        lock (runtime.SyncRoot)
        {
            runtime.WorkerRunning = false;

            if (runtime.Pending)
            {
                runtime.WorkerRunning = true;
                _ = Task.Run(() => ProcessQueueAsync(runtime, CancellationToken.None));
            }
        }
    }

    private void LogCommandResult(string repositoryPath, string args, GitCommandResult result)
    {
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Git実行成功: repoPath={RepositoryPath}, command=git {Args}, stdout={Stdout}",
                repositoryPath,
                args,
                result.StandardOutput);
            return;
        }

        _logger.LogError(
            "Git実行失敗: repoPath={RepositoryPath}, command=git {Args}, exitCode={ExitCode}, stdout={Stdout}, stderr={Stderr}",
            repositoryPath,
            args,
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }

    private sealed class RepositoryRuntime
    {
        public RepositoryRuntime(RepositorySyncDefinition definition)
        {
            Definition = definition;
        }

        public RepositorySyncDefinition Definition { get; set; }

        public object SyncRoot { get; } = new();

        public bool WorkerRunning { get; set; }

        public bool Pending { get; set; }

        public bool IsPaused { get; set; }

        public int RetryCount { get; set; }

        public bool HasNotifiedTransientFailure { get; set; }
    }
}
