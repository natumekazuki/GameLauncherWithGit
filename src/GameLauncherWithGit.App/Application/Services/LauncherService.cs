using System.Diagnostics;
using GameLauncherWithGit.App.Application.Abstractions;
using GameLauncherWithGit.App.Application.Models;
using GameLauncherWithGit.App.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.App.Application.Services;

public sealed class LauncherService : ILauncherService
{
    private readonly IGameLibraryService _gameLibraryService;
    private readonly INotificationService _notificationService;
    private readonly ILogger<LauncherService> _logger;

    public LauncherService(
        IGameLibraryService gameLibraryService,
        INotificationService notificationService,
        ILogger<LauncherService> logger)
    {
        _gameLibraryService = gameLibraryService;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<LaunchResult> LaunchAsync(GameItem game, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(game.ExecutablePath))
        {
            return LaunchResult.Failure($"実行ファイルが見つかりません: {game.ExecutablePath}");
        }

        try
        {
            string workingDirectory = Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty;
            var startInfo = new ProcessStartInfo
            {
                FileName = game.ExecutablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
            };

            Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return LaunchResult.Failure("ゲームの起動に失敗しました。");
            }

            await _gameLibraryService.MarkLaunchedAsync(game.Id, cancellationToken).ConfigureAwait(false);
            await _notificationService.NotifyInfoAsync("ゲーム起動", $"{game.Title} を起動しました。", cancellationToken).ConfigureAwait(false);

            return LaunchResult.Success("ゲームを起動しました。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ゲーム起動失敗: gameId={GameId}", game.Id);
            return LaunchResult.Failure("ゲーム起動中にエラーが発生しました。ログを確認してください。");
        }
    }
}
