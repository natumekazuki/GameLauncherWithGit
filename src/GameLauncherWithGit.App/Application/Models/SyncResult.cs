using GameLauncherWithGit.App.Infrastructure.Models;

namespace GameLauncherWithGit.App.Application.Models;

public sealed record SyncResult(
    bool IsSuccess,
    string Command,
    SyncFailureKind FailureKind,
    GitCommandResult? CommandResult,
    string Message)
{
    public static SyncResult Success() => new(
        true,
        string.Empty,
        SyncFailureKind.None,
        null,
        "同期が完了しました。");

    public static SyncResult Failure(
        string command,
        SyncFailureKind failureKind,
        GitCommandResult commandResult,
        string message) => new(
        false,
        command,
        failureKind,
        commandResult,
        message);

    public bool IsTransientFailure => FailureKind == SyncFailureKind.Network;
}
