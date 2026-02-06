using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class GitService : IGitService
{
	private readonly ILogger<GitService> _logger;

	public GitService(ILogger<GitService> logger)
	{
		_logger = logger;
	}

	public async Task<GitCommandResult> RunAsync(
		string repositoryPath,
		string arguments,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
		{
			return new GitCommandResult(-1, string.Empty, $"リポジトリパスが存在しません: {repositoryPath}");
		}

		var startInfo = new ProcessStartInfo
		{
			FileName = "git",
			WorkingDirectory = repositoryPath,
			Arguments = arguments,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var process = new Process
		{
			StartInfo = startInfo
		};

		try
		{
			_logger.LogInformation("Git command start. cwd={RepositoryPath}, args={Arguments}", repositoryPath, arguments);
			process.Start();

			var readStdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var readStdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);

			var standardOutput = await readStdOutTask;
			var standardError = await readStdErrTask;

			_logger.LogInformation(
				"Git command end. cwd={RepositoryPath}, args={Arguments}, exitCode={ExitCode}",
				repositoryPath,
				arguments,
				process.ExitCode);

			return new GitCommandResult(process.ExitCode, standardOutput, standardError);
		}
		catch (OperationCanceledException)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
				}
			}
			catch (InvalidOperationException)
			{
			}

			return new GitCommandResult(-2, string.Empty, "Git 実行がキャンセルされました。");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Git command failed. cwd={RepositoryPath}, args={Arguments}", repositoryPath, arguments);
			return new GitCommandResult(-1, string.Empty, ex.Message);
		}
	}
}
