using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class LauncherServiceTests
{
    private const string StartedMessage = "ゲームを起動しました。";

    // @test-value v2
    // kind = "invariant"
    // claim = "ゲーム起動後の履歴保存や記録開始の失敗は、起動済みという事実や他の後処理を取り消さない"
    // oracle = { type = "contract", ref = "Session Graph app-wide-launch-result-boundary; src/GameLauncherWithGit/Application/Abstractions/ILauncherService.cs: LaunchAsync" }
    // fault = "起動後の例外を起動失敗として返す、履歴失敗で記録開始を飛ばす、または正常な記録無効を失敗とする"
    // observable = "起動結果の成功・警告・画面遷移と、記録開始の呼び出し"
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
        var executable = Path.GetTempFileName();
        try
        {
            var deps = new LaunchDependencies(executable, historyFails);
            var captureCalls = 0;
            var launcher = new LauncherService(deps, deps, deps, NullLogger<LauncherService>.Instance,
                _ =>
                {
                    captureCalls++;
                    return captureFails
                        ? Task.FromException<bool>(new IOException("capture unavailable"))
                        : Task.FromResult(showCapture);
                },
                _ => new LaunchResult(true, StartedMessage));

            var result = await launcher.LaunchAsync("fixture");

            Assert.True(result.IsSuccess);
            Assert.Equal(showCapture, result.ShowCapture);
            Assert.Equal(1, captureCalls);
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
        }
        finally
        {
            File.Delete(executable);
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
        var launcher = new LauncherService(deps, deps, deps, NullLogger<LauncherService>.Instance,
            _ => throw new InvalidOperationException("Capture must not run"),
            _ => throw new InvalidOperationException("Launch must not run"));

        var result = await launcher.LaunchAsync("fixture");

        Assert.False(result.IsSuccess);
        Assert.Contains("実行ファイルが見つかりません", result.Message);
        Assert.Equal(0, deps.SaveLinkPrepareCalls);
        Assert.Equal(0, deps.GitCalls);
        Assert.Empty(deps.Statuses);
    }

    private sealed class LaunchDependencies(string executable, bool historyFails)
        : IGameLibraryService, ISaveLinkService, IGitService
    {
        public List<GameCardStatus> Statuses { get; } = [];
        public int SaveLinkPrepareCalls { get; private set; }
        public int GitCalls { get; private set; }

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

        public Task MarkLaunchedAsync(string gameId, CancellationToken cancellationToken = default) =>
            historyFails ? Task.FromException(new IOException("history unavailable")) : Task.CompletedTask;

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
