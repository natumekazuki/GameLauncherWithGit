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

public sealed class LauncherServiceTests : IAsyncDisposable
{
    private const string StartedMessage = "ゲームを起動しました。";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("launcher-tests-");
    private readonly SqliteGameLibraryStore _store;

    public LauncherServiceTests()
    {
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory.FullName, "capture.db"));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "起動後の履歴・記録の失敗は起動済みと他の後処理を保ち、表示側が通常成功と警告付き成功を文言に頼らず区別できる"
    // oracle = { type = "contract", ref = "Session Graph ui-card-error-launch-warning-boundary（通常成功を省いても起動後警告を表示する）; app-wide-core-review-required-fixes; app-wide-launch-result-boundary" }
    // fault = "後処理の例外を起動失敗にする、履歴失敗で記録開始を飛ばす、警告を型で返さない、または正常な記録無効にも警告を付ける"
    // observable = "起動結果のIsSuccess・HasWarnings・Message・ShowCapture、履歴書込の試行回数、実記録サービスの選択待ちと既存boolの結果"
    // observation_boundary = "public-boundary"
    // scope = "game launch result after reported OS startup"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(true, false, true, "起動履歴の保存")]
    [InlineData(false, true, false, "シナリオ記録の開始")]
    [InlineData(false, false, false, null)]
    public async Task StartedGameRemainsStartedWhenFollowUpFails(
        bool historyFails, bool captureFails, bool showCapture, string? warning)
    {
        var executable = Path.Combine(_directory.FullName, "fixture.exe");
        await File.WriteAllTextAsync(executable, "not an executable");
        if (!captureFails)
        {
            await _store.UpsertAsync(new("fixture", "fixture", executable, null, null, null, GameCardStatus.Unknown));
            var profile = await _store.GetOrCreateCaptureProfileAsync("fixture");
            await _store.SaveCaptureProfileAsync(profile with { Enabled = showCapture });
        }

        // When the launch lookup has a game but capture cannot load a registration, the real capture boundary fails.
        await using var capture = new ScenarioCaptureService(_store, new UnusedHost(),
            NullLogger<ScenarioCaptureService>.Instance);
        var deps = new LaunchDependencies(executable, historyFails);
        var launcher = new LauncherService(deps, deps, deps, NullLogger<LauncherService>.Instance, capture,
            _ => new LaunchResult(true, StartedMessage));

        var result = await launcher.LaunchAsync("fixture");

        Assert.True(result.IsSuccess);
        Assert.Equal(warning is not null, result.HasWarnings);
        Assert.Equal(showCapture, result.ShowCapture);
        Assert.Equal(showCapture, capture.Snapshot.NeedsProcess);
        Assert.Equal(1, deps.MarkLaunchedCalls);
        Assert.Equal(new[] { GameCardStatus.Syncing, GameCardStatus.Synced }, deps.Statuses);
        if (warning is null)
        {
            Assert.Equal(StartedMessage, result.Message);
        }
        else
        {
            Assert.StartsWith(StartedMessage, result.Message);
            Assert.Contains(warning + "に失敗しました", result.Message);
        }

        if (captureFails)
        {
            Assert.False(await capture.BeginGameAsync("fixture"));
        }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "実行ファイルが無いゲームは起動前のリンク準備とGit同期を実行しない"
    // oracle = { type = "contract", ref = "src/GameLauncherWithGit/Application/Abstractions/ILauncherService.cs: LaunchAsync" }
    // fault = "起動できない登録に対して、実行ファイルを確認する前にリンクやリポジトリへ作用する"
    // observable = "起動失敗の理由と、リンク準備・Git・プロセス起動の呼び出し有無"
    // observation_boundary = "public-boundary"
    // scope = "launch prerequisites before side effects"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task MissingExecutableStopsBeforePreparingSaveLinks()
    {
        var deps = new LaunchDependencies(string.Empty, historyFails: false);
        await using var capture = new ScenarioCaptureService(_store, new UnusedHost(),
            NullLogger<ScenarioCaptureService>.Instance);
        var processStarts = 0;
        var launcher = new LauncherService(deps, deps, deps, NullLogger<LauncherService>.Instance, capture,
            _ =>
            {
                processStarts++;
                return new LaunchResult(true, StartedMessage);
            });

        var result = await launcher.LaunchAsync("fixture");

        Assert.False(result.IsSuccess);
        Assert.Contains("実行ファイルが見つかりません", result.Message);
        Assert.Equal(0, deps.SaveLinkPrepareCalls);
        Assert.Equal(0, deps.GitCalls);
        Assert.Equal(0, processStarts);
        Assert.Empty(deps.Statuses);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory.FullName, "capture.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ConnectionString);
        SqliteConnection.ClearPool(connection);
        _directory.Delete(recursive: true);
    }

    private sealed class UnusedHost : ILunaCaptureHost
    {
        public event Action<CaptureSource, string>? Output { add { } remove { } }
        public event Action<uint, bool>? ConnectionChanged { add { } remove { } }
        public Task ConnectAsync(int processId) => throw new InvalidOperationException("No process is available in the fixture.");
        public void Detach(int processId) => throw new InvalidOperationException("No process is connected.");
        public IReadOnlyList<CaptureProcess> GetProcesses() => [];
        public CaptureProcess? GetForegroundProcess() => null;
    }

    private sealed class LaunchDependencies(string executable, bool historyFails)
        : IGameLibraryService, ISaveLinkService, IGitService
    {
        public List<GameCardStatus> Statuses { get; } = [];
        public int SaveLinkPrepareCalls { get; private set; }
        public int GitCalls { get; private set; }
        public int MarkLaunchedCalls { get; private set; }

        public Task<GameCardItem?> FindByIdAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameCardItem?>(new(gameId, gameId, executable,
                executable.Length == 0 ? "fixture-repository" : null, null, null, GameCardStatus.Unknown));

        public Task<SaveLinkPrepareResult> EnsureReadyForLaunchAsync(string gameId, CancellationToken cancellationToken = default)
        {
            SaveLinkPrepareCalls++;
            return Task.FromResult(SaveLinkPrepareResult.Success);
        }

        public Task SetStatusAsync(string gameId, GameCardStatus status, CancellationToken cancellationToken = default)
        {
            Statuses.Add(status);
            return Task.CompletedTask;
        }

        public Task MarkLaunchedAsync(string gameId, CancellationToken cancellationToken = default)
        {
            MarkLaunchedCalls++;
            return historyFails ? Task.FromException(new IOException("history unavailable")) : Task.CompletedTask;
        }

        public Task<IReadOnlyList<GameCardItem>> GetGamesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GameCardItem> CreateAsync(GameEditInput input, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GameCardItem> CreateWithSaveLinksAsync(GameEditInput input, IReadOnlyList<GameSaveLinkEditInput> saveLinks,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GameCardItem?> UpdateAsync(string gameId, GameEditInput input,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GameCardItem?> UpdateWithSaveLinksAsync(string gameId, GameEditInput input,
            IReadOnlyList<GameSaveLinkEditInput> saveLinks, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string gameId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPinnedAsync(string gameId, bool isPinned, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GameSaveLinkItem>> GetByGameIdAsync(string gameId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<GameSaveLinkItem> NormalizeForGame(string gameId,
            IReadOnlyList<GameSaveLinkEditInput> links) => throw new NotSupportedException();
        public void ValidateForGame(string gameId, IReadOnlyList<GameSaveLinkEditInput> links) => throw new NotSupportedException();
        public Task<IReadOnlyList<GameSaveLinkItem>> UnlinkRemovedLinksAsync(string gameId, IReadOnlyList<GameSaveLinkItem> nextLinks,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> RollbackUnlinkedLinksAsync(string gameId, IReadOnlyList<GameSaveLinkItem> unlinkedLinks,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceForGameAsync(string gameId, IReadOnlyList<GameSaveLinkEditInput> links,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            GitCalls++;
            throw new InvalidOperationException("Git must not run");
        }
        public Task<GitCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
