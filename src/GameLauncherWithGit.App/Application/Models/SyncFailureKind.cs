namespace GameLauncherWithGit.App.Application.Models;

public enum SyncFailureKind
{
    None = 0,
    Conflict = 1,
    Authentication = 2,
    Network = 3,
    Permission = 4,
    Unknown = 5,
}
