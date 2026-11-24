using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class GameLibraryRegistrationTests : IAsyncDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("game-registration-");
    private readonly SqliteGameLibraryStore _store;

    public GameLibraryRegistrationTests()
    {
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory.FullName, "library.db"));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "新規登録の成功後、ゲームとセーブリンク定義を読み出せるが実リンクは存在しない"
    // oracle = { type = "contract", ref = "docs/design/game-save-link-manager.md §8.1 ゲーム新規作成時の保存整合性" }
    // fault = "ゲームだけ登録してリンク定義を保存しない、または保存時にリンクを適用する"
    // observable = "ストアから読み出した新規行とリンク定義、リンク操作代替の呼び出し数、対象パスの有無"
    // observation_boundary = "public-boundary"
    // scope = "SQLite game registration with save links"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task CreatePersistsGameAndLinkDefinitionWithoutApplyingLink()
    {
        var game = Game("new");
        var link = Link("link", game.Id, 0);

        var dependencies = new RegistrationDependencies();
        var saveLinks = new SaveLinkService(_store, dependencies, NullLogger<SaveLinkService>.Instance);
        var games = new GameLibraryService(_store, saveLinks, dependencies, dependencies,
            NullLogger<GameLibraryService>.Instance, Path.Combine(_directory.FullName, "thumbnails"));

        var created = await games.CreateWithSaveLinksAsync(new(game.Title, "", null, null, false),
            [new(link.Id, link.DisplayName, link.LocalPath, link.TargetPath, link.EnsureOnLaunch)]);

        Assert.Equal(game, created);
        Assert.Equal(game, await _store.FindByIdAsync(game.Id));
        Assert.Equal(link, Assert.Single(await _store.GetByGameIdAsync(game.Id)));
        Assert.Equal(0, dependencies.LinkOperations);
        Assert.Null(new DirectoryInfo(link.LocalPath).LinkTarget);
        Assert.False(Directory.Exists(link.LocalPath));
        Assert.False(Directory.Exists(link.TargetPath));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "リンク書き込みが途中で失敗しても新規ゲームと先行リンクは残らず、既存登録は保持される"
    // oracle = { type = "contract", ref = "docs/design/game-save-link-manager.md §8.1 ゲーム新規作成時の保存整合性" }
    // fault = "ゲームまたは先行リンクを別取引で確定したままリンク挿入に失敗する"
    // observable = "リンク ID 衝突での保存失敗とストアから読み出した新規・既存ゲームおよびリンク"
    // observation_boundary = "public-boundary"
    // scope = "SQLite transaction rollback during game registration"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ConflictingLinkIdRollsBackGameAndEarlierLink()
    {
        var existing = Game("existing");
        var existingLink = Link("taken", existing.Id, 0);
        await _store.InsertWithSaveLinksAsync(existing, [existingLink]);
        var game = Game("new");

        var failure = await Assert.ThrowsAsync<SqliteException>(() =>
            _store.InsertWithSaveLinksAsync(game, [Link("first", game.Id, 0), Link(existingLink.Id, game.Id, 1)]));

        Assert.Equal(19, failure.SqliteErrorCode);
        Assert.Null(await _store.FindByIdAsync(game.Id));
        Assert.Empty(await _store.GetByGameIdAsync(game.Id));
        Assert.Equal(existing, await _store.FindByIdAsync(existing.Id));
        Assert.Equal(existingLink, Assert.Single(await _store.GetByGameIdAsync(existing.Id)));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "先行リンク解除後に編集を中断すると、元のリンク定義を保持し、復元可能な先行解除分を復元する"
    // oracle = { type = "contract", ref = "docs/design/game-save-link-manager.md §8.2; src/GameLauncherWithGit/Application/Abstractions/ISaveLinkService.cs: UnlinkRemovedLinksAsync" }
    // fault = "呼び出しがキャンセルされたため、既に解除したリンクの復元まで省略する"
    // observable = "中断例外、ローカル操作代替の有効リンク集合とSQLiteのリンク定義"
    // observation_boundary = "component-behavior"
    // scope = "save-link edit cancellation and compensation"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task CanceledUnlinkRestoresEarlierRemoval()
    {
        var game = Game("saved");
        GameSaveLinkItem[] links = [Link("first", game.Id, 0), Link("second", game.Id, 1)];
        await _store.InsertWithSaveLinksAsync(game, links);
        using var cancel = new CancellationTokenSource();
        var filesystem = new CancelAfterFirstUnlink(links, cancel);
        var service = new SaveLinkService(_store, filesystem, NullLogger<SaveLinkService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.UnlinkRemovedLinksAsync(game.Id, [], cancel.Token));

        Assert.All(links, link => Assert.Contains(link.LocalPath, filesystem.ActiveLinks));
        Assert.Equal(links, await _store.GetByGameIdAsync(game.Id));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "通常フォルダがある場合の復元は手動確認を返してローカルセーブを保持し、未存在の場合だけ再作成へ進む"
    // oracle = { type = "contract", ref = "docs/design/game-save-link-manager.md §8.2 リンク解除フロー（ローカル通常フォルダ復元）" }
    // fault = "解除後の通常フォルダを削除してリンクを再作成する、または復元できないのに成功とする"
    // observable = "復元結果、手動確認の文言、合成ファイルの内容、再作成代替へのパス、旧DB定義"
    // observation_boundary = "component-behavior"
    // scope = "save-link rollback with existing local directory or missing local path"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestorePreservesSaveDataAndOnlyRecreatesAtMissingLocalPath(bool localExists)
    {
        var game = Game("saved");
        var link = Link("first", game.Id, 0);
        await _store.InsertWithSaveLinksAsync(game, [link]);
        Directory.CreateDirectory(link.TargetPath);
        var targetSave = Path.Combine(link.TargetPath, "save.dat");
        await File.WriteAllTextAsync(targetSave, "original");
        var localSave = Path.Combine(link.LocalPath, "save.dat");
        if (localExists)
        {
            Directory.CreateDirectory(link.LocalPath);
            await File.WriteAllTextAsync(localSave, "saved after unlink");
        }

        var creates = new List<(string Local, string Target)>();
        var filesystem = new LocalSaveLinkOperator(NullLogger<LocalSaveLinkOperator>.Instance, (local, target, _) =>
        {
            creates.Add((local, target));
            return Task.FromResult(new JunctionEnsureResult(true, "fixture"));
        });
        var saveLinks = new SaveLinkService(_store, filesystem, NullLogger<SaveLinkService>.Instance);

        var error = await saveLinks.RollbackUnlinkedLinksAsync(game.Id, [link]);

        if (localExists)
        {
            Assert.Contains("手動で確認", error);
            Assert.Equal("saved after unlink", await File.ReadAllTextAsync(localSave));
            Assert.Empty(creates);
        }
        else
        {
            Assert.Null(error);
            Assert.Equal((link.LocalPath, link.TargetPath), Assert.Single(creates));
        }

        Assert.Equal("original", await File.ReadAllTextAsync(targetSave));
        Assert.Equal(link, Assert.Single(await _store.GetByGameIdAsync(game.Id)));
    }

    private sealed class RegistrationDependencies : ILocalSaveLinkOperator, IGitService, IThumbnailService
    {
        public int LinkOperations { get; private set; }

        private Task<T> UnexpectedLinkOperation<T>()
        {
            LinkOperations++;
            throw new InvalidOperationException("Registration must not operate on links.");
        }

        public Task<JunctionEnsureResult> EnsureJunctionAsync(string localPath, string targetPath, CancellationToken cancellationToken) =>
            UnexpectedLinkOperation<JunctionEnsureResult>();
        public Task<JunctionRemoveResult> RemoveJunctionWithRestoreAsync(string localPath, string targetPath, CancellationToken cancellationToken) =>
            UnexpectedLinkOperation<JunctionRemoveResult>();
        public Task<JunctionEnsureResult> RestoreJunctionAsync(string localPath, string targetPath, CancellationToken cancellationToken) =>
            UnexpectedLinkOperation<JunctionEnsureResult>();
        public Task<DirectoryHydrationResult> HydrateDirectoryAsync(string targetPath, CancellationToken cancellationToken) =>
            UnexpectedLinkOperation<DirectoryHydrationResult>();
        public Task<GitCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string?> CreateThumbnailAsync(string sourceImagePath, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CancelAfterFirstUnlink(GameSaveLinkItem[] links, CancellationTokenSource cancellation)
        : ILocalSaveLinkOperator
    {
        public HashSet<string> ActiveLinks { get; } = links.Select(link => link.LocalPath).ToHashSet();

        public Task<JunctionRemoveResult> RemoveJunctionWithRestoreAsync(string localPath, string targetPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveLinks.Remove(localPath);
            cancellation.Cancel();
            return Task.FromResult(new JunctionRemoveResult(true, "removed", DidChangeLocalPath: true));
        }

        public Task<JunctionEnsureResult> RestoreJunctionAsync(string localPath, string targetPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveLinks.Add(localPath);
            return Task.FromResult(new JunctionEnsureResult(true, "restored"));
        }

        public Task<JunctionEnsureResult> EnsureJunctionAsync(string localPath, string targetPath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DirectoryHydrationResult> HydrateDirectoryAsync(string targetPath,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static GameCardItem Game(string id) =>
        new(id, id, string.Empty, null, null, null, GameCardStatus.Unknown);

    private GameSaveLinkItem Link(string id, string gameId, int order) =>
        new(id, gameId, id, Path.Combine(_directory.FullName, $"{gameId}-local-{order}"),
            Path.Combine(_directory.FullName, $"{gameId}-target-{order}"), order, true);

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory.FullName, "library.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ConnectionString);
        SqliteConnection.ClearPool(connection);
        _directory.Delete(recursive: true);
    }
}
