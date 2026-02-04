using GameLauncherWithGit.App.Application.Abstractions;
using GameLauncherWithGit.App.Application.Models;
using GameLauncherWithGit.App.Infrastructure.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.App.Pages;

public partial class Index : ComponentBase
{
    private readonly HashSet<Guid> _loadingThumbnails = new();

    private bool _isLoading = true;
    private bool _isEditorOpen;
    private bool _isDetailOpen;

    private Guid? _editingGameId;
    private GameItem? _selectedGame;

    private GameDraft _draft = new();
    private IReadOnlyList<GameItem> _games = Array.Empty<GameItem>();
    private string? _initializeErrorMessage;

    [Inject] private IGameLibraryService GameLibraryService { get; set; } = default!;
    [Inject] private ILauncherService LauncherService { get; set; } = default!;
    [Inject] private INotificationService NotificationService { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private ILogger<Index> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            await ReloadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Index初期化失敗");
            _initializeErrorMessage = "画面の初期化に失敗しました。アプリログを確認してください。";
            _isLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ReloadAsync()
    {
        _games = await GameLibraryService.GetAllAsync().ConfigureAwait(false);
        _loadingThumbnails.Clear();

        foreach (GameItem game in _games)
        {
            if (!string.IsNullOrWhiteSpace(game.ThumbnailPath))
            {
                _loadingThumbnails.Add(game.Id);
            }
        }

        _isLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    private void OpenCreateModal()
    {
        _draft = new GameDraft();
        _editingGameId = null;
        _isEditorOpen = true;
    }

    private void OpenDetails(GameItem game)
    {
        _selectedGame = game;
        _isDetailOpen = true;
    }

    private void CloseDetails()
    {
        _isDetailOpen = false;
        _selectedGame = null;
    }

    private void OpenEditFromDetails()
    {
        if (_selectedGame is null)
        {
            return;
        }

        _draft = new GameDraft
        {
            Title = _selectedGame.Title,
            ExecutablePath = _selectedGame.ExecutablePath,
            ThumbnailSourcePath = _selectedGame.ThumbnailPath,
            RelatedRepositoryPath = _selectedGame.RelatedRepositoryPath,
        };

        _editingGameId = _selectedGame.Id;
        _isEditorOpen = true;
        _isDetailOpen = false;
    }

    private void CloseEditor()
    {
        _isEditorOpen = false;
        _draft = new GameDraft();
        _editingGameId = null;
    }

    private async Task SaveDraftAsync()
    {
        if (string.IsNullOrWhiteSpace(_draft.Title))
        {
            await NotificationService.NotifyErrorAsync("入力エラー", "タイトルは必須です。").ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(_draft.ExecutablePath))
        {
            await NotificationService.NotifyErrorAsync("入力エラー", "実行ファイルを選択してください。").ConfigureAwait(false);
            return;
        }

        try
        {
            if (_editingGameId is null)
            {
                await GameLibraryService.AddAsync(_draft).ConfigureAwait(false);
            }
            else
            {
                await GameLibraryService.UpdateAsync(_editingGameId.Value, _draft).ConfigureAwait(false);
            }

            CloseEditor();
            await ReloadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await LogUiErrorAsync("保存処理", ex).ConfigureAwait(false);
        }
    }

    private async Task PickExecutablePathAsync()
    {
        IPathPickerService? picker = TryResolvePathPickerService();
        if (picker is null)
        {
            await NotificationService.NotifyErrorAsync("処理エラー", "ファイルピッカーを初期化できません。ログを確認してください。").ConfigureAwait(false);
            return;
        }

        try
        {
            string? selectedPath = await picker.PickExecutablePathAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _draft.ExecutablePath = selectedPath;
            }
        }
        catch (Exception ex)
        {
            await LogUiErrorAsync("実行ファイル選択", ex).ConfigureAwait(false);
        }
    }

    private async Task PickThumbnailImagePathAsync()
    {
        IPathPickerService? picker = TryResolvePathPickerService();
        if (picker is null)
        {
            await NotificationService.NotifyErrorAsync("処理エラー", "画像ピッカーを初期化できません。ログを確認してください。").ConfigureAwait(false);
            return;
        }

        try
        {
            string? selectedPath = await picker.PickThumbnailImagePathAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _draft.ThumbnailSourcePath = selectedPath;
            }
        }
        catch (Exception ex)
        {
            await LogUiErrorAsync("サムネイル選択", ex).ConfigureAwait(false);
        }
    }

    private async Task PickRepositoryFolderPathAsync()
    {
        IPathPickerService? picker = TryResolvePathPickerService();
        if (picker is null)
        {
            await NotificationService.NotifyErrorAsync("処理エラー", "フォルダピッカーを初期化できません。ログを確認してください。").ConfigureAwait(false);
            return;
        }

        try
        {
            string? selectedPath = await picker.PickRepositoryFolderPathAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                _draft.RelatedRepositoryPath = selectedPath;
            }
        }
        catch (Exception ex)
        {
            await LogUiErrorAsync("関連リポジトリ選択", ex).ConfigureAwait(false);
        }
    }

    private void ClearExecutablePath()
    {
        _draft.ExecutablePath = string.Empty;
    }

    private void ClearThumbnailPath()
    {
        _draft.ThumbnailSourcePath = null;
    }

    private void ClearRepositoryPath()
    {
        _draft.RelatedRepositoryPath = null;
    }

    private async Task DeleteSelectedAsync()
    {
        if (_selectedGame is null)
        {
            return;
        }

        await GameLibraryService.DeleteAsync(_selectedGame.Id).ConfigureAwait(false);
        CloseDetails();
        await ReloadAsync().ConfigureAwait(false);
    }

    private async Task LaunchAsync(GameItem game)
    {
        LaunchResult result = await LauncherService.LaunchAsync(game).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            await NotificationService.NotifyErrorAsync("ゲーム起動失敗", result.Message).ConfigureAwait(false);
        }

        await ReloadAsync().ConfigureAwait(false);
    }

    private async Task SyncNowAsync()
    {
        await NotificationService.NotifyInfoAsync("今すぐ同期", "同期処理は次のステップで実装します。").ConfigureAwait(false);
        CloseDetails();
    }

    private async Task OpenLogAsync()
    {
        await NotificationService.NotifyInfoAsync("ログ", "ログ表示機能は次のステップで実装します。").ConfigureAwait(false);
        CloseDetails();
    }

    private void OnThumbnailLoaded(Guid gameId)
    {
        _loadingThumbnails.Remove(gameId);
    }

    private void OnThumbnailFailed(Guid gameId)
    {
        _loadingThumbnails.Remove(gameId);
    }

    private static string ToFileUri(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return string.Empty;
        }

        return $"file:///{localPath.Replace('\\', '/')}";
    }

    private static string FormatLastPlayed(DateTimeOffset? value)
    {
        return value is null ? "最終プレイ: 未記録" : $"最終プレイ: {value.Value:yyyy-MM-dd HH:mm}";
    }

    private static string FormatRepositoryPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? "関連リポジトリ: 未設定" : $"関連リポジトリ: {path}";
    }

    private async Task LogUiErrorAsync(string actionName, Exception ex)
    {
        Logger.LogError(ex, "UI例外: {ActionName}", actionName);

        await NotificationService.NotifyErrorAsync(
            "処理中にエラーが発生しました",
            $"{actionName} に失敗しました。アプリログを確認してください。").ConfigureAwait(false);
    }

    private IPathPickerService? TryResolvePathPickerService()
    {
        try
        {
            IPathPickerService? service = Services.GetService<IPathPickerService>();
            if (service is null)
            {
                Logger.LogError("IPathPickerService のDI解決に失敗しました。サービス未登録の可能性があります。");
            }

            return service;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "IPathPickerService のDI解決中に例外が発生しました");
            return null;
        }
    }

    private static string GetStatusClass(GameSyncStatus status)
    {
        return status switch
        {
            GameSyncStatus.Syncing => "status-syncing",
            GameSyncStatus.Synced => "status-synced",
            GameSyncStatus.Error => "status-error",
            _ => "status-unknown",
        };
    }

    private static string GetStatusLabel(GameSyncStatus status)
    {
        return status switch
        {
            GameSyncStatus.Syncing => "同期中",
            GameSyncStatus.Synced => "同期OK",
            GameSyncStatus.Error => "エラー",
            _ => "未同期",
        };
    }
}
