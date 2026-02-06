using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Application.Services;

public sealed class LauncherService : ILauncherService
{
	private readonly IGameLibraryService _gameLibraryService;
	private readonly ISyncOrchestrator _syncOrchestrator;
	private readonly ILogger<LauncherService> _logger;

	public LauncherService(
		IGameLibraryService gameLibraryService,
		ISyncOrchestrator syncOrchestrator,
		ILogger<LauncherService> logger)
	{
		_gameLibraryService = gameLibraryService;
		_syncOrchestrator = syncOrchestrator;
		_logger = logger;
	}

	public async Task<LaunchResult> LaunchAsync(string gameId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return new LaunchResult(false, "ゲームIDが不正です。");
		}

		try
		{
			await _gameLibraryService.SetStatusAsync(gameId, GameCardStatus.Syncing, cancellationToken);
			await _syncOrchestrator.QueueRepositorySyncAsync(gameId, cancellationToken);
			await _gameLibraryService.SetStatusAsync(gameId, GameCardStatus.Synced, cancellationToken);
			await _gameLibraryService.MarkLaunchedAsync(gameId, cancellationToken);

			_logger.LogInformation("Launch placeholder completed. gameId={GameId}", gameId);
			return new LaunchResult(true, "起動前同期（プレースホルダー）が完了しました。");
		}
		catch (OperationCanceledException)
		{
			_logger.LogWarning("Launch canceled. gameId={GameId}", gameId);
			return new LaunchResult(false, "処理がキャンセルされました。");
		}
		catch (Exception ex)
		{
			await _gameLibraryService.SetStatusAsync(gameId, GameCardStatus.Error, cancellationToken);
			_logger.LogError(ex, "Launch placeholder failed. gameId={GameId}", gameId);
			return new LaunchResult(false, "起動前同期に失敗しました。ログを確認してください。");
		}
	}
}
