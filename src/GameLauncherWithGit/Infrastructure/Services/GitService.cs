using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class GitService : IGitService
{
	private static readonly Version MinimumGitVersion = new(2, 38, 0);
	private readonly ILogAccessService _logAccessService;
	private readonly ILogger<GitService> _logger;

	public GitService(ILogAccessService logAccessService, ILogger<GitService> logger)
	{
		_logAccessService = logAccessService;
		_logger = logger;
	}

	public async Task<GitCommandResult> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
	{
		string[] arguments = ["--version"];
		var result = await ExecuteAsync(null, null, arguments, cancellationToken);
		if (result.IsSuccess)
		{
			var match = Regex.Match(result.StandardOutput, @"\bgit version (\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant);
			if (!match.Success || !Version.TryParse($"{match.Groups[1]}.{match.Groups[2]}.{match.Groups[3]}", out var version))
			{
				result = new GitCommandResult(-1, result.StandardOutput, result.StandardError,
					"Git のバージョンを確認できません。Git 2.38.0 以降をインストールしてください。");
			}
			else if (version < MinimumGitVersion)
			{
				result = new GitCommandResult(-1, result.StandardOutput, result.StandardError,
					$"Git {version} は対応していません。Git 2.38.0 以降に更新してください。");
			}
		}

		return await RecordFailureAsync(null, arguments, result);
	}

	public async Task<GitCommandResult> RunAsync(
		string repositoryPath,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken = default)
	{
		string root;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			root = ResolveDirectoryPath(repositoryPath);
			if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
			{
				throw new DirectoryNotFoundException($"ディレクトリではありません: {root}");
			}

			File.GetAttributes(Path.Combine(root, ".git"));
		}
		catch (OperationCanceledException)
		{
			return new GitCommandResult(-2, string.Empty, "Git 実行がキャンセルされました。");
		}
		catch (UnauthorizedAccessException ex)
		{
			return await RecordFailureAsync(repositoryPath, arguments, new GitCommandResult(-1, string.Empty, ex.ToString(),
				"リポジトリへのアクセスが拒否されました。アクセス権を確認してください。"));
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			return await RecordFailureAsync(repositoryPath, arguments, new GitCommandResult(-1, string.Empty, ex.ToString(),
				"Git リポジトリを確認できません。.git のある作業ツリーのルートを選択してください。"));
		}
		catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
		{
			return await RecordFailureAsync(repositoryPath, arguments, new GitCommandResult(-1, string.Empty, ex.ToString(),
				"リポジトリのパスを確認できません。フォルダとアクセス権を確認してください。"));
		}

		string[] rootArguments = ["rev-parse", "--show-toplevel"];
		var rootResult = await ExecuteAsync(root, root, rootArguments, cancellationToken);
		if (!rootResult.IsSuccess)
		{
			if (rootResult.StandardError.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase))
			{
				var availability = await CheckAvailabilityAsync(cancellationToken);
				if (availability.ExitCode == -2)
				{
					return availability;
				}

				if (!availability.IsSuccess)
				{
					rootResult = rootResult with { UserMessage = availability.GetFailureReason() };
				}
			}

			return await RecordFailureAsync(repositoryPath, rootArguments, rootResult);
		}

		try
		{
			var actualRoot = ResolveDirectoryPath(rootResult.StandardOutput.Trim());
			var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
			if (!string.Equals(root, actualRoot, comparison))
			{
				return await RecordFailureAsync(repositoryPath, rootArguments, new GitCommandResult(-1, rootResult.StandardOutput,
					$"Selected directory: {root}{Environment.NewLine}Git worktree root: {actualRoot}",
					"選択したフォルダと Git の作業ツリーのルートが一致しません。ルートを選択し直してください。"));
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return await RecordFailureAsync(repositoryPath, rootArguments, new GitCommandResult(-1, rootResult.StandardOutput, ex.ToString(),
				"Git の作業ツリーのルートを確認できません。フォルダとアクセス権を確認してください。"));
		}

		var result = await ExecuteAsync(root, root, arguments, cancellationToken);
		return await RecordFailureAsync(repositoryPath, arguments, result);
	}

	private static string ResolveDirectoryPath(string path)
	{
		var fullPath = Path.GetFullPath(path);
		var pathRoot = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("パスのルートを確認できません。", nameof(path));
		var currentPath = pathRoot;
		foreach (var part in fullPath[pathRoot.Length..].Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
		{
			currentPath = Path.Combine(currentPath, part);
			var target = new DirectoryInfo(currentPath).ResolveLinkTarget(returnFinalTarget: true);
			if (target is not null)
			{
				currentPath = target.FullName;
			}
		}

		return Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
	}

	private async Task<GitCommandResult> ExecuteAsync(
		string? workingDirectory,
		string? safeDirectory,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "git",
			WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		if (safeDirectory is not null)
		{
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add($"safe.directory={safeDirectory.Replace('\\', '/')}");
		}

		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = new Process { StartInfo = startInfo };
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			_logger.LogInformation("Git command start. cwd={RepositoryPath}, args={Arguments}", workingDirectory, arguments);
			process.Start();
			var readStdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var readStdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);
			var standardOutput = await readStdOutTask;
			var standardError = await readStdErrTask;
			_logger.LogInformation("Git command end. cwd={RepositoryPath}, args={Arguments}, exitCode={ExitCode}",
				workingDirectory, arguments, process.ExitCode);
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
			_logger.LogError(ex, "Git command failed. cwd={RepositoryPath}, args={Arguments}", workingDirectory, arguments);
			return new GitCommandResult(-1, string.Empty, ex.ToString(),
				"Git の起動または実行に失敗しました。Git のインストールとアクセス権を確認してください。");
		}
	}

	private async Task<GitCommandResult> RecordFailureAsync(
		string? repositoryPath,
		IReadOnlyList<string> arguments,
		GitCommandResult result)
	{
		if (result.IsSuccess || result.ExitCode == -2
			|| (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.StandardError) && string.IsNullOrWhiteSpace(result.StandardOutput)))
		{
			return result;
		}

		try
		{
			await _logAccessService.AppendGitErrorAsync(repositoryPath, arguments, result);
			return result with { UserMessage = $"{result.GetFailureReason()} 詳細は環境設定のログを確認してください。" };
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to record Git error. repositoryPath={RepositoryPath}", repositoryPath);
			return result with { UserMessage = $"{result.GetFailureReason()} Git の詳細をログに保存できませんでした。" };
		}
	}
}
