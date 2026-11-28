using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace GameLauncherWithGit.Components.Pages;

public partial class Capture
{
    private ElementReference _rowMenu;
    private RowDeletion? _rowDeletion;
    private string? _openRowMenuViewKey;

    /// <summary>Protects the selected row from being replaced while its menu is open.</summary>
    [JSInvokable]
    public void RowMenuVisibility(string viewKey, bool open)
    {
        if (_displayedScope?.ViewKey == viewKey) _openRowMenuViewKey = open ? viewKey : null;
    }

    /// <summary>Freezes the scope and rendered row for a single-row confirmation.</summary>
    private sealed record RowDeletion(DisplayedScope Scope, CaptureOccurrenceDeletionTarget Target);

    /// <summary>Revalidates the originating row before opening the existing history confirmation.</summary>
    [JSInvokable]
    public Task OpenRowDeletionFromMenuAsync(string viewKey, string rowId)
    {
        if (_displayedScope is not { } scope || scope.ViewKey != viewKey ||
            RequestedGameId != _loadedGameId || _busy || _flowOpen || EditorOpen || _pickingRange || !long.TryParse(rowId, out var id) ||
            _items.FirstOrDefault(item => item.Id == id) is not { } item) return Task.CompletedTask;

        return RunAsync(async () =>
        {
            await RequireCurrentScopeAsync(scope);
            var target = await Store.GetCaptureOccurrenceDeletionTargetAsync(scope.RecordId,
                new(item.Id, item.StartPosition, item.EndPosition));
            await RequireCurrentScopeAsync(scope);
            // A late preview must not turn this menu into a deletion of another row or view.
            if (RequestedGameId != _loadedGameId) return;
            if (!_items.Contains(item)) throw ScopeChanged();
            _rowDeletion = new(scope, target);
            _historyDialog = HistoryDialog.DeleteRow;
            _focusEditor = true;
        }, failureLabel: "本文行を確認できませんでした");
    }

    /// <summary>Deletes only the confirmed occurrence through the existing desktop history gate.</summary>
    private Task ConfirmRowDeletionAsync(RowDeletion row, PlayRecordingTarget target) => RunAsync(async () =>
    {
        if (_historyDialog != HistoryDialog.DeleteRow || !ReferenceEquals(row, _rowDeletion)) throw ScopeChanged();

        await WithHistoryWriteAsync(row.Scope, target, async recordId =>
        {
            if (row.Target.RecordId != recordId || RequestedGameId != _loadedGameId) throw ScopeChanged();
            if (HistoryCaptureActive) throw new InvalidOperationException("取得を停止してから削除してください。");
            // The store compares the full occurrence identity again before writing.
            _screenshotCleanup = (await Store.DeleteCaptureOccurrenceAsync(recordId, row.Target)).ScreenshotCleanup;
        });
        if (!await CanPublishAsync(row.Scope)) return;

        // Retire only the deleted row's state; retain the reader's query, page and viewport.
        var itemId = row.Target.Item.Id;
        _selected.Remove(itemId);
        if (_detailItem?.Id == itemId) ClearDetail();
        if (_edit?.Id == itemId) DiscardCorrection();
        _flowNeedsReload = _flowGraph is not null;
        CloseEditors();
        await LoadItemsAsync();
        _message = "本文行を削除しました。";
    }, failureLabel: "本文行を削除できませんでした");
}
