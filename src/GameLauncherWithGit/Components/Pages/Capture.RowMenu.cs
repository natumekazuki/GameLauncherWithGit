using GameLauncherWithGit.Application.Models;
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
    private sealed record RowDeletion(DisplayedScope Scope, CaptureTextItem Item);

    /// <summary>Revalidates the originating row before opening the existing history confirmation.</summary>
    [JSInvokable]
    public Task OpenRowDeletionFromMenuAsync(string viewKey, string rowId)
    {
        if (_displayedScope is not { } scope || scope.ViewKey != viewKey ||
            _busy || _flowOpen || EditorOpen || _pickingRange || !long.TryParse(rowId, out var id) ||
            _items.FirstOrDefault(item => item.Id == id) is not { } item) return Task.CompletedTask;

        return RunAsync(async () =>
        {
            await RequireCurrentScopeAsync(scope);
            // A wait for a running refresh must not turn this menu into a deletion of another row or view.
            if (!_items.Contains(item)) throw ScopeChanged();
            _rowDeletion = new(scope, item);
            _historyDialog = HistoryDialog.DeleteRow;
            _focusEditor = true;
        }, failureLabel: "本文行を確認できませんでした");
    }
}
