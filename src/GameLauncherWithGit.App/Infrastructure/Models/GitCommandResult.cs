namespace GameLauncherWithGit.App.Infrastructure.Models;

public sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}
