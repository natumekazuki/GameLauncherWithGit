namespace GameLauncherWithGit.App.Application.Models;

public sealed record LaunchResult(bool IsSuccess, string Message)
{
    public static LaunchResult Success(string message) => new(true, message);

    public static LaunchResult Failure(string message) => new(false, message);
}
