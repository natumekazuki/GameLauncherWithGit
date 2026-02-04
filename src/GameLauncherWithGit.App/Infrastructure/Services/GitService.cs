using System.Diagnostics;
using System.Text;
using GameLauncherWithGit.App.Infrastructure.Abstractions;
using GameLauncherWithGit.App.Infrastructure.Models;

namespace GameLauncherWithGit.App.Infrastructure.Services;

public sealed class GitService : IGitService
{
    public async Task<GitCommandResult> ExecuteAsync(
        string repositoryPath,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("リポジトリパスは必須です。", nameof(repositoryPath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        return new GitCommandResult(process.ExitCode, NormalizeOutput(stdout), NormalizeOutput(stderr));
    }

    private static string NormalizeOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        return new StringBuilder(output.Trim()).ToString();
    }
}
