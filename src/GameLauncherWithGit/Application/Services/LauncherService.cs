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

		var pullResult = await _gitService.RunAsync(repositoryPath, "pull --rebase", cancellationToken);
		if (!pullResult.IsSuccess)
		{
			return BuildFailureResult(repositoryPath, "pull --rebase", pullResult);
		}

		return new LaunchResult(true, "起動前同期に成功しました。");
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
}
