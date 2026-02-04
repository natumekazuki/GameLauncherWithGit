using System.Collections.Concurrent;
using GameLauncherWithGit.App.Infrastructure.Abstractions;
using GameLauncherWithGit.App.Infrastructure.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.App.Infrastructure.Services;

public sealed class RepositoryWatcherService : IRepositoryWatcherService, IDisposable
{
    private readonly ConcurrentDictionary<string, WatchRegistration> _registrations = new();
    private readonly ILogger<RepositoryWatcherService> _logger;

    public RepositoryWatcherService(ILogger<RepositoryWatcherService> logger)
    {
        _logger = logger;
    }

    public event EventHandler<RepositoryChangedEventArgs>? RepositoryChanged;

    public void StartWatch(string repositoryId, string path, int debounceSeconds)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            throw new ArgumentException("リポジトリIDは必須です。", nameof(repositoryId));
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"監視対象ディレクトリが見つかりません: {path}");
        }

        int debounceMilliseconds = Math.Max(1, debounceSeconds) * 1000;

        StopWatch(repositoryId);

        var registration = new WatchRegistration(repositoryId, path, debounceMilliseconds, OnDebouncedChanged);
        _registrations[repositoryId] = registration;

        _logger.LogInformation("監視開始: repositoryId={RepositoryId}, path={Path}, debounceMs={DebounceMs}", repositoryId, path, debounceMilliseconds);
    }

    public void StopWatch(string repositoryId)
    {
        if (_registrations.TryRemove(repositoryId, out var registration))
        {
            registration.Dispose();
            _logger.LogInformation("監視停止: repositoryId={RepositoryId}", repositoryId);
        }
    }

    public void Dispose()
    {
        foreach (var pair in _registrations)
        {
            pair.Value.Dispose();
        }

        _registrations.Clear();
    }

    private void OnDebouncedChanged(RepositoryChangedEventArgs args)
    {
        RepositoryChanged?.Invoke(this, args);
    }

    private sealed class WatchRegistration : IDisposable
    {
        private readonly string _repositoryId;
        private readonly string _path;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _debounceTimer;
        private readonly int _debounceMilliseconds;
        private readonly Action<RepositoryChangedEventArgs> _onChanged;

        private int _dirty;
        private bool _disposed;

        public WatchRegistration(
            string repositoryId,
            string path,
            int debounceMilliseconds,
            Action<RepositoryChangedEventArgs> onChanged)
        {
            _repositoryId = repositoryId;
            _path = path;
            _debounceMilliseconds = debounceMilliseconds;
            _onChanged = onChanged;

            _debounceTimer = new Timer(OnDebounceElapsed, state: null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                Filter = "*.*",
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Deleted -= OnFileChanged;
            _watcher.Dispose();
            _debounceTimer.Dispose();
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _debounceTimer.Change(_debounceMilliseconds, Timeout.Infinite);
        }

        private void OnDebounceElapsed(object? state)
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0)
            {
                return;
            }

            _onChanged(new RepositoryChangedEventArgs(_repositoryId, _path));
        }
    }
}
