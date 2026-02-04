namespace GameLauncherWithGit.App.Application.Models;

public sealed record RepositorySyncDefinition(
    string RepositoryId,
    string RepositoryPath,
    string WatchPath,
    int DebounceSeconds = 10);
