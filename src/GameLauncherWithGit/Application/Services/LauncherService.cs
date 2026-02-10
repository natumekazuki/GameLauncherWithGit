using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace GameLauncherWithGit.Application.Services;

public sealed class LauncherService : ILauncherService
{
	private readonly IGameLibraryService _gameLibraryService;
	private readonly IGitService _gitService;
	private readonly ILogger<LauncherService> _logger;

	public LauncherService(
		IGameLibraryService gameLibraryService,
		IGitService gitService,
		ILogger<LauncherService> logger)
	{
		_gameLibraryService = gameLibraryService;
		_gitService = gitService;
		_logger = logger;
	}

	public async Task<LaunchResult> LaunchAsync(string gameId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return new LaunchResult(false, "ゲームIDが不正です。");
		}

		var game = await _gameLibraryService.FindByIdAsync(gameId, cancellationToken);
		if (game is null)
		{
			return new LaunchResult(false, $"ゲームが見つかりません: {gameId}");
		}

		try
		{
			await _gameLibraryService.SetStatusAsync(game.Id, GameCardStatus.Syncing, cancellationToken);

			var syncResult = await SynchronizeBeforeLaunchAsync(game, cancellationToken);
			if (!syncResult.IsSuccess)
			{
				await _gameLibraryService.SetStatusAsync(game.Id, GameCardStatus.Error, cancellationToken);
				return syncResult;
			}

			var launchResult = StartGameProcess(game);
			if (!launchResult.IsSuccess)
			{
				await _gameLibraryService.SetStatusAsync(game.Id, GameCardStatus.Error, cancellationToken);
				return launchResult;
			}

			await _gameLibraryService.SetStatusAsync(game.Id, GameCardStatus.Synced, cancellationToken);
			await _gameLibraryService.MarkLaunchedAsync(game.Id, cancellationToken);

			return new LaunchResult(true, "起動前同期が完了し、ゲームを起動しました。");
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Launch canceled. gameId={GameId}", gameId);
			return new LaunchResult(false, "処理がキャンセルされました。");
		}
		catch (Exception ex)
		{
			await _gameLibraryService.SetStatusAsync(game.Id, GameCardStatus.Error, cancellationToken);
			_logger.LogError(ex, "Launch failed unexpectedly. gameId={GameId}", gameId);
			return new LaunchResult(false, "起動前同期に失敗しました。ログを確認してください。");
		}
	}

	private async Task<LaunchResult> SynchronizeBeforeLaunchAsync(GameCardItem game, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(game.RelatedRepositoryPath))
		{
			_logger.LogInformation("Related repository is not configured. gameId={GameId}", game.Id);
			return new LaunchResult(true, "関連リポジトリ未設定のため、同期をスキップして起動します。");
		}

		var repositoryPath = game.RelatedRepositoryPath;
		var fetchResult = await _gitService.RunAsync(repositoryPath, "fetch", cancellationToken);
		if (!fetchResult.IsSuccess)
		{
			return BuildFailureResult(repositoryPath, "fetch", fetchResult);
		}

		var remoteAheadResult = await TryGetRemoteAheadCountAsync(repositoryPath, cancellationToken);
		if (!remoteAheadResult.IsSuccess)
		{
			return BuildFailureResult(repositoryPath, "rev-list --left-right --count HEAD...@{upstream}", remoteAheadResult.Result);
		}

		if (remoteAheadResult.RemoteAheadCount <= 0)
		{
			var addResult = await _gitService.RunAsync(repositoryPath, "add -A", cancellationToken);
			if (!addResult.IsSuccess)
			{
				return BuildFailureResult(repositoryPath, "add -A", addResult);
			}

			var statusResult = await _gitService.RunAsync(repositoryPath, "status --porcelain", cancellationToken);
			if (!statusResult.IsSuccess)
			{
				return BuildFailureResult(repositoryPath, "status --porcelain", statusResult);
			}

			if (!string.IsNullOrWhiteSpace(statusResult.StandardOutput))
			{
				var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
				var commitResult = await _gitService.RunAsync(
					repositoryPath,
					$"commit -m \"auto: save sync {timestamp}\"",
					cancellationToken);
				if (!commitResult.IsSuccess && !IsNothingToCommit(commitResult))
				{
					return BuildFailureResult(repositoryPath, "commit -m", commitResult);
				}
			}
		}
		else
		{
			_logger.LogInformation(
				"Skip local auto-commit because remote is ahead. gameId={GameId}, repositoryPath={RepositoryPath}, remoteAheadCount={RemoteAheadCount}",
				game.Id,
				repositoryPath,
				remoteAheadResult.RemoteAheadCount);
		}

		var pullResult = await PullWithConfiguredTargetAsync(repositoryPath, cancellationToken);
		if (!pullResult.IsSuccess)
		{
			return BuildFailureResult(repositoryPath, "pull --rebase --autostash", pullResult);
		}

		return new LaunchResult(true, "起動前同期に成功しました。");
	}

	private async Task<RemoteAheadResult> TryGetRemoteAheadCountAsync(
		string repositoryPath,
		CancellationToken cancellationToken)
	{
		var result = await _gitService.RunAsync(
			repositoryPath,
			"rev-list --left-right --count HEAD...@{upstream}",
			cancellationToken);
		if (!result.IsSuccess)
		{
			var detail = $"{result.StandardError}\n{result.StandardOutput}";
			if (detail.Contains("no upstream configured", StringComparison.OrdinalIgnoreCase)
				|| detail.Contains("upstream branch", StringComparison.OrdinalIgnoreCase)
				|| detail.Contains("追跡ブランチ", StringComparison.OrdinalIgnoreCase)
				|| IsUnbornBranchError(detail))
			{
				_logger.LogInformation(
					"Upstream is not ready. Treat remote-ahead count as 0. repositoryPath={RepositoryPath}",
					repositoryPath);
				return new RemoteAheadResult(true, 0, result);
			}

			return new RemoteAheadResult(false, 0, result);
		}

		if (!TryParseAheadBehindCount(result.StandardOutput, out _, out var remoteAheadCount))
		{
			_logger.LogWarning(
				"Failed to parse ahead/behind output. repositoryPath={RepositoryPath}, output={Output}",
				repositoryPath,
				result.StandardOutput);
			return new RemoteAheadResult(false, 0, result);
		}

		return new RemoteAheadResult(true, remoteAheadCount, result);
	}

	private static bool TryParseAheadBehindCount(string output, out int localAheadCount, out int remoteAheadCount)
	{
		localAheadCount = 0;
		remoteAheadCount = 0;

		var firstLine = FirstNonEmptyLine(output);
		if (string.IsNullOrWhiteSpace(firstLine))
		{
			return false;
		}

		var parts = firstLine
			.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length < 2)
		{
			return false;
		}

		return int.TryParse(parts[0], out localAheadCount)
			&& int.TryParse(parts[1], out remoteAheadCount);
	}

	private LaunchResult StartGameProcess(GameCardItem game)
	{
		if (!File.Exists(game.ExecutablePath))
		{
			return new LaunchResult(false, $"実行ファイルが見つかりません: {game.ExecutablePath}");
		}

		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = game.ExecutablePath,
				WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath),
				UseShellExecute = true
			};

			var process = Process.Start(startInfo);
			if (process is null)
			{
				return new LaunchResult(false, "ゲームプロセスの起動に失敗しました。");
			}

			_logger.LogInformation("Game process started. gameId={GameId}, path={ExecutablePath}", game.Id, game.ExecutablePath);
			return new LaunchResult(true, "ゲームを起動しました。");
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Game process start failed. gameId={GameId}", game.Id);
			return new LaunchResult(false, $"ゲーム起動に失敗しました: {ex.Message}");
		}
	}

	private static LaunchResult BuildFailureResult(string repositoryPath, string command, GitCommandResult result)
	{
		var reason = FirstNonEmptyLine(result.StandardError)
			?? FirstNonEmptyLine(result.StandardOutput)
			?? $"exit code: {result.ExitCode}";

		var message = $"起動前同期に失敗しました。repo={repositoryPath}, command=git {command}, reason={reason}";
		return new LaunchResult(false, message);
	}

	private static string? FirstNonEmptyLine(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		return value
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
	}

	private static bool IsNothingToCommit(GitCommandResult result)
	{
		var text = $"{result.StandardError}\n{result.StandardOutput}";
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		return text.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("no changes added to commit", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("作業ツリーはクリーン", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("コミットするものがありません", StringComparison.OrdinalIgnoreCase);
	}

	private async Task<GitCommandResult> PullWithConfiguredTargetAsync(
		string repositoryPath,
		CancellationToken cancellationToken)
	{
		var pullTarget = await TryBuildPullCommandAsync(repositoryPath, cancellationToken);
		if (pullTarget.IsDetachedHead)
		{
			_logger.LogWarning(
				"Skip launch sync because repository is detached HEAD. repositoryPath={RepositoryPath}",
				repositoryPath);
			return new GitCommandResult(1, string.Empty, "skip pull: detached HEAD");
		}

		if (string.IsNullOrWhiteSpace(pullTarget.Command))
		{
			_logger.LogInformation(
				"Skip pull because upstream is not configured. repositoryPath={RepositoryPath}",
				repositoryPath);
			return new GitCommandResult(0, "skip pull: no upstream", string.Empty);
		}

		return await _gitService.RunAsync(repositoryPath, pullTarget.Command, cancellationToken);
	}

	private async Task<PullCommandBuildResult> TryBuildPullCommandAsync(
		string repositoryPath,
		CancellationToken cancellationToken)
	{
		var branchResult = await _gitService.RunAsync(repositoryPath, "branch --show-current", cancellationToken);
		if (!branchResult.IsSuccess)
		{
			return PullCommandBuildResult.NoUpstream;
		}

		var branchName = FirstNonEmptyLine(branchResult.StandardOutput);
		if (string.IsNullOrWhiteSpace(branchName))
		{
			return PullCommandBuildResult.DetachedHead;
		}

		var remoteResult = await _gitService.RunAsync(
			repositoryPath,
			$"config --get branch.{branchName}.remote",
			cancellationToken);
		if (!remoteResult.IsSuccess)
		{
			return PullCommandBuildResult.NoUpstream;
		}

		var remoteName = FirstNonEmptyLine(remoteResult.StandardOutput);
		if (string.IsNullOrWhiteSpace(remoteName))
		{
			return PullCommandBuildResult.NoUpstream;
		}

		var mergeResult = await _gitService.RunAsync(
			repositoryPath,
			$"config --get-all branch.{branchName}.merge",
			cancellationToken);
		if (!mergeResult.IsSuccess)
		{
			return PullCommandBuildResult.NoUpstream;
		}

		var mergeCandidates = GetNonEmptyLines(mergeResult.StandardOutput);
		if (mergeCandidates.Count == 0)
		{
			return PullCommandBuildResult.NoUpstream;
		}

		if (mergeCandidates.Count > 1)
		{
			_logger.LogWarning(
				"Multiple merge targets detected. Use first target for pull. repositoryPath={RepositoryPath}, branch={BranchName}, mergeTargets={MergeTargets}",
				repositoryPath,
				branchName,
				string.Join(", ", mergeCandidates));
		}

		var mergeTarget = NormalizeMergeTarget(mergeCandidates[0]);
		if (string.IsNullOrWhiteSpace(mergeTarget))
		{
			return PullCommandBuildResult.NoUpstream;
		}

		return new PullCommandBuildResult($"pull --rebase --autostash {remoteName} {mergeTarget}", false);
	}

	private sealed record PullCommandBuildResult(string? Command, bool IsDetachedHead)
	{
		public static PullCommandBuildResult NoUpstream { get; } = new(null, false);
		public static PullCommandBuildResult DetachedHead { get; } = new(null, true);
	}

	private static string NormalizeMergeTarget(string mergeTarget)
	{
		const string HeadsPrefix = "refs/heads/";
		var normalized = mergeTarget.Trim();
		return normalized.StartsWith(HeadsPrefix, StringComparison.OrdinalIgnoreCase)
			? normalized[HeadsPrefix.Length..]
			: normalized;
	}

	private static IReadOnlyList<string> GetNonEmptyLines(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return [];
		}

		return value
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(static line => !string.IsNullOrWhiteSpace(line))
			.ToArray();
	}

	private static bool IsUnbornBranchError(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
		{
			return false;
		}

		return detail.Contains("no such branch: 'HEAD...'", StringComparison.OrdinalIgnoreCase)
			|| detail.Contains("ambiguous argument 'HEAD...@'", StringComparison.OrdinalIgnoreCase)
			|| detail.Contains("needed a single revision", StringComparison.OrdinalIgnoreCase);
	}

	private sealed record RemoteAheadResult(
		bool IsSuccess,
		int RemoteAheadCount,
		GitCommandResult Result);
}
