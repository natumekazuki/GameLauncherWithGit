using GameLauncherWithGit.App.Application.Models;
using GameLauncherWithGit.App.Application.Services;
using GameLauncherWithGit.App.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameLauncherWithGit.App.Tests;

public class GameLibraryServiceTests
{
    [Fact]
    public async Task AddAsync_WithValidDraft_SetsTrimmedValuesAndThumbnailPath()
    {
        using var fixture = new TestFixture(thumbnailResult: true, createThumbnailFile: true);
        var service = fixture.CreateService();

        var added = await service.AddAsync(new GameDraft
        {
            Title = "  Test Game  ",
            ExecutablePath = "  C:\\Games\\test.exe  ",
            ThumbnailSourcePath = "C:\\tmp\\source.png",
            RelatedRepositoryIdsCsv = "repo-a, repo-b, repo-a",
        });

        Assert.Equal("Test Game", added.Title);
        Assert.Equal("C:\\Games\\test.exe", added.ExecutablePath);
        Assert.Equal(new[] { "repo-a", "repo-b" }, added.RelatedRepositoryIds);
        Assert.False(string.IsNullOrWhiteSpace(added.ThumbnailPath));
        Assert.True(File.Exists(added.ThumbnailPath!));
    }

    [Fact]
    public async Task AddAsync_WithoutTitle_ThrowsInvalidOperationException()
    {
        using var fixture = new TestFixture();
        var service = fixture.CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(new GameDraft
        {
            Title = "   ",
            ExecutablePath = "C:\\Games\\test.exe",
        }));
    }

    [Fact]
    public async Task UpdateAsync_WithUnknownGame_ThrowsInvalidOperationException()
    {
        using var fixture = new TestFixture();
        var service = fixture.CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(Guid.NewGuid(), new GameDraft
        {
            Title = "Game",
            ExecutablePath = "C:\\Games\\test.exe",
        }));
    }

    [Fact]
    public async Task GetAllAsync_ReturnsSortedClones()
    {
        using var fixture = new TestFixture();
        var service = fixture.CreateService();

        await service.AddAsync(new GameDraft { Title = "zeta", ExecutablePath = "C:\\Games\\zeta.exe" });
        await service.AddAsync(new GameDraft { Title = "alpha", ExecutablePath = "C:\\Games\\alpha.exe" });

        var list = await service.GetAllAsync();
        Assert.Equal(new[] { "alpha", "zeta" }, list.Select(x => x.Title).ToArray());

        list[0].Title = "changed";
        var list2 = await service.GetAllAsync();
        Assert.Equal("alpha", list2[0].Title);
    }

    [Fact]
    public async Task DeleteAsync_WithGeneratedThumbnail_RemovesThumbnailFile()
    {
        using var fixture = new TestFixture(thumbnailResult: true, createThumbnailFile: true);
        var service = fixture.CreateService();

        var added = await service.AddAsync(new GameDraft
        {
            Title = "Game",
            ExecutablePath = "C:\\Games\\test.exe",
            ThumbnailSourcePath = "C:\\tmp\\source.png",
        });

        Assert.True(File.Exists(added.ThumbnailPath!));
        await service.DeleteAsync(added.Id);
        Assert.False(File.Exists(added.ThumbnailPath!));
    }

    [Fact]
    public async Task MarkLaunchedAsync_UpdatesLastPlayedAt()
    {
        using var fixture = new TestFixture();
        var service = fixture.CreateService();

        var added = await service.AddAsync(new GameDraft
        {
            Title = "Game",
            ExecutablePath = "C:\\Games\\test.exe",
        });

        await service.MarkLaunchedAsync(added.Id);
        var stored = (await service.GetAllAsync()).Single(x => x.Id == added.Id);
        Assert.NotNull(stored.LastPlayedAt);
    }

    private sealed class TestFixture : IDisposable
    {
        private readonly string _baseDir;
        private readonly FakeAppStoragePaths _paths;
        private readonly FakeThumbnailService _thumbnailService;

        public TestFixture(bool thumbnailResult = true, bool createThumbnailFile = false)
        {
            _baseDir = Path.Combine(Path.GetTempPath(), "GameLauncherWithGit.Tests", Guid.NewGuid().ToString("N"));
            _paths = new FakeAppStoragePaths(_baseDir);
            _thumbnailService = new FakeThumbnailService(thumbnailResult, createThumbnailFile);
        }

        public GameLibraryService CreateService()
        {
            return new GameLibraryService(_paths, _thumbnailService, NullLogger<GameLibraryService>.Instance);
        }

        public void Dispose()
        {
            if (Directory.Exists(_baseDir))
            {
                Directory.Delete(_baseDir, recursive: true);
            }
        }
    }

    private sealed class FakeAppStoragePaths : IAppStoragePaths
    {
        public FakeAppStoragePaths(string baseDir)
        {
            BaseDirectory = baseDir;
            SettingsFilePath = Path.Combine(baseDir, "settings.json");
            LogDirectory = Path.Combine(baseDir, "logs");
            ThumbnailDirectory = Path.Combine(baseDir, "thumbnails");
            EnsureCreated();
        }

        public string BaseDirectory { get; }

        public string SettingsFilePath { get; }

        public string LogDirectory { get; }

        public string ThumbnailDirectory { get; }

        public void EnsureCreated()
        {
            Directory.CreateDirectory(BaseDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(ThumbnailDirectory);
        }
    }

    private sealed class FakeThumbnailService : IThumbnailService
    {
        private readonly bool _result;
        private readonly bool _createFile;

        public FakeThumbnailService(bool result, bool createFile)
        {
            _result = result;
            _createFile = createFile;
        }

        public Task<bool> TryGenerateThumbnailAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        {
            if (_result && _createFile)
            {
                string? dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllBytes(destinationPath, new byte[] { 1, 2, 3 });
            }

            return Task.FromResult(_result);
        }
    }
}
