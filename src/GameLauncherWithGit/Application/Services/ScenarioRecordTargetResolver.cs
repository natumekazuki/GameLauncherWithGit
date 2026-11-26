using GameLauncherWithGit.Infrastructure.Abstractions;

namespace GameLauncherWithGit.Application.Services;

/// <summary>Resolves the worktree's current recording root without changing Git or synchronization settings.</summary>
public sealed class ScenarioRecordTargetResolver(IGameLibraryStore games, IScenarioCaptureStore profiles,
    IGitService git, IScenarioHistoryStore history)
{
    /// <summary>Returns the current root, or null if the game, its profile, or its configured Git HEAD cannot be resolved.</summary>
    public async Task<long?> GetCurrentRecordIdAsync(string gameId)
    {
        var profile = await profiles.FindCaptureProfileAsync(gameId);
        if (profile is null) return null;
        var path = await ReadRepositoryPathAsync(gameId, CancellationToken.None);
        using var target = await ResolveAsync(path, profile.Id, CancellationToken.None);
        return target.IsCurrent ? target.RecordId : null;
    }

    /// <summary>Owns a history display's current-branch observation until that display releases it.</summary>
    internal async Task<DesktopHistoryObservation> ObserveDesktopHistoryAsync(string gameId, long profileId, CancellationToken token)
    {
        var setting = await ReadRepositoryPathAsync(gameId, token);
        return new(this, gameId, profileId, await ResolveAsync(setting, profileId, token));
    }

    private async Task<bool> IsCurrentProfileAsync(string gameId, long profileId) =>
        (await profiles.FindCaptureProfileAsync(gameId))?.Id == profileId;

    internal sealed class DesktopHistoryObservation(ScenarioRecordTargetResolver resolver, string gameId, long profileId,
        ResolvedRecord record) : IDisposable
    {
        private int _disposed;

        public string GameId { get; } = gameId;
        public long ProfileId { get; } = profileId;
        public long? RecordId => Volatile.Read(ref _disposed) == 0 && record.IsCurrent ? record.RecordId : null;

        public async Task<bool> IsCurrentAsync(string expectedGameId, long expectedRecordId, CancellationToken token = default)
        {
            if (GameId != expectedGameId || RecordId != expectedRecordId) return false;
            if (!await resolver.IsCurrentProfileAsync(GameId, ProfileId)
                || await resolver.ReadRepositoryPathAsync(GameId, token) != record.Setting)
            {
                Dispose();
                return false;
            }
            return RecordId == expectedRecordId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) record.Dispose();
        }
    }

    internal async Task<bool> OwnsRecordAsync(string gameId, long recordId)
    {
        var profile = await profiles.FindCaptureProfileAsync(gameId);
        return profile is not null && await OwnsRecordAsync(profile.Id, recordId);
    }

    internal async Task<bool> OwnsRecordAsync(long profileId, long recordId)
    {
        try
        {
            return (await history.GetScenarioHistoryScopesAsync(profileId))
                .Any(scope => scope.ProfileId == profileId && scope.RecordId == recordId);
        }
        catch (ArgumentException) { return false; }
    }

    internal async Task<RepositorySetting> ReadRepositoryPathAsync(string gameId, CancellationToken token)
    {
        var game = await games.FindByIdAsync(gameId, token);
        return new(game is not null, string.IsNullOrWhiteSpace(game?.RelatedRepositoryPath) ? null : game.RelatedRepositoryPath);
    }

    internal async Task<ResolvedRecord> ResolveAsync(RepositorySetting setting, long profileId, CancellationToken token)
    {
        if (!setting.GameExists) return new(setting, null);
        if (setting.Path is null) return new(setting, profileId);
        try
        {
            var location = await git.RunAsync(setting.Path,
                ["rev-parse", "--path-format=absolute", "--git-common-dir", "--git-path", "HEAD"], token);
            if (!location.IsSuccess) return new(setting, null);
            var paths = location.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (paths.Length != 2 || !paths.All(Path.IsPathFullyQualified)) return new(setting, null);

            var commonDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths[0]));
            var repositoryKey = OperatingSystem.IsWindows() ? commonDirectory.ToUpperInvariant() : commonDirectory;
            var headPath = Path.GetFullPath(paths[1]);
            HeadWatch? watch = new(headPath);
            try
            {
                var branch = watch.Branch;
                long? recordId = null;
                if (branch is not null && watch.IsCurrent
                    && (await git.RunAsync(setting.Path, ["check-ref-format", branch], token)).IsSuccess
                    && watch.IsCurrent)
                    recordId = await history.GetOrCreateBranchRecordIdAsync(profileId, repositoryKey, branch);
                var resolved = new ResolvedRecord(setting, recordId, watch);
                watch = null;
                return resolved;
            }
            finally { watch?.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(setting, null);
        }
    }

    internal readonly record struct RepositorySetting(bool GameExists, string? Path);

    internal sealed class ResolvedRecord(RepositorySetting setting, long? recordId, HeadWatch? watch = null) : IDisposable
    {
        public RepositorySetting Setting { get; } = setting;
        public long? RecordId { get; } = recordId;
        // An invalidated watch is never revalidated even if HEAD returns to the same symbolic ref.
        public bool IsCurrent => RecordId is not null && ObservationIsCurrent;
        public bool ObservationIsCurrent => watch?.IsCurrent ?? (Setting.GameExists && Setting.Path is null);
        public void Dispose() => watch?.Dispose();
    }

    internal sealed class HeadWatch : IDisposable
    {
        private readonly string _path;
        private readonly FileSystemWatcher _watcher;
        private readonly DateTime? _lastWrite;
        private int _invalidated;

        public HeadWatch(string path)
        {
            _path = path;
            _watcher = new(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };
            _watcher.Changed += (_, _) => Invalidate();
            _watcher.Created += (_, _) => Invalidate();
            _watcher.Deleted += (_, _) => Invalidate();
            _watcher.Renamed += (_, _) => Invalidate();
            _watcher.Error += (_, _) => Invalidate();
            try
            {
                _watcher.EnableRaisingEvents = true;
                _lastWrite = ReadLastWrite(path);
                Branch = ReadBranch(path);
            }
            catch { _watcher.Dispose(); throw; }
        }

        public string? Branch { get; }

        public bool IsCurrent
        {
            get
            {
                if (Volatile.Read(ref _invalidated) != 0) return false;
                if (ReadBranch(_path) != Branch || ReadLastWrite(_path) != _lastWrite) Invalidate();
                return Volatile.Read(ref _invalidated) == 0;
            }
        }

        private static DateTime? ReadLastWrite(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private static string? ReadBranch(string path)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length > 4096) return null;
                using var reader = new StreamReader(file);
                var text = reader.ReadToEnd().TrimEnd('\r', '\n');
                const string prefix = "ref: refs/heads/";
                if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length == prefix.Length
                    || text.AsSpan(prefix.Length).ContainsAny('\r', '\n', '\0')) return null;
                return text[5..];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);

        public void Dispose()
        {
            Invalidate();
            _watcher.Dispose();
        }
    }
}
