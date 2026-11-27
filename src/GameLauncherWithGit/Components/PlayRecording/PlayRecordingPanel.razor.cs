using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace GameLauncherWithGit.Components.PlayRecording;

/// <summary>Record controls for a connected Web circuit following the PC target.</summary>
public partial class PlayRecordingPanel
{
    [Inject] private ILogger<PlayRecordingPanel> Logger { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private enum Pane { Text, Paths }
    private enum FocusTarget { FinishImpact, FinishButton, Pause, Resume, Screenshot, RangeStatus, RangeStart, RangePicker, RangeDraft, RangeEditor, Ranges, Classification, EditButton, PathRow, Gallery, GalleryButton, Text }
    private (FocusTarget Target, long ItemId)? _nextFocus;
    private ElementReference _finishImpact, _finishButton, _rangeStatus, _rangeDraftButton, _rangeEditorHeading,
        _startRangeButton, _pickRangeButton, _rangesTab, _editClassification, _galleryHeading, _screenshotButton;
    private ScenarioRecordingBar? _recordingBar;
    private sealed class RowButtons { public ElementReference Open; }
    private readonly Dictionary<long, RowButtons> _rowButtons = [];

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _pollRead;
    private long _displayedConnectionVersion;
    private object _workspace = new();
    private object _shellKey = new();
    private readonly record struct DisplayTarget(string GameId, PlayRecordingTarget Recording, long ConnectionVersion, object Workspace);
    private DisplayTarget ForDisplay(PlayRecordingState state) => new(state.GameId, state.Target, _displayedConnectionVersion, _workspace);
    private sealed class DisplayChangedException : Exception { }
    private bool _busy, _disposed, _statusLoaded, _statusError, _paneNeedsLoad;
    private sealed record OperationFailure(string Label, string Detail);
    private OperationFailure? _actionError;
    private string? _actionSuccess;
    private Pane _pane;
    private bool _savedRangesOpen;
    private bool _scrollToLatest;
    private ElementReference _textScroller;

    private bool ControlsDisabled => _busy || _statusError || _state is null;
    private static string Bool(bool value) => value ? "true" : "false";
    private string Pressed(Pane pane) => Bool(_pane == pane);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Prerender has no live Interactive Server connection to read from.
        if (firstRender)
        {
            await RefreshAsync();
            if (!_disposed) _ = PollAsync(_lifetime.Token);
            return;
        }
        if (_pane == Pane.Text && _galleryItem is null && _textScroller.Id is not null && !_statusError)
        {
            try
            {
                if (_restoreReadingPosition is { } top)
                {
                    _restoreReadingPosition = null;
                    await Js.InvokeVoidAsync("playRecordingText.restore", _textScroller, top);
                }
                else if (_scrollToLatest)
                {
                    _scrollToLatest = false;
                    await Js.InvokeVoidAsync("playRecordingText.toEnd", _textScroller);
                }
            }
            catch (JSDisconnectedException) { }
        }
        if (_nextFocus is not { } request || _busy) return;
        _nextFocus = null;
        if (ControlsDisabled || _disposed) return;
        if (request.Target is FocusTarget.Pause or FocusTarget.Resume)
        {
            try { if (_recordingBar is not null) await _recordingBar.FocusPauseAsync(); }
            catch (JSDisconnectedException) { }
            return;
        }

        var textVisible = _pane == Pane.Text && _galleryItem is null;
        var rowVisible = textVisible && !_pickingRange && !_textError;
        var row = _rowButtons.GetValueOrDefault(request.ItemId);
        var destination = request.Target switch
        {
            FocusTarget.FinishImpact when _finishConfirmation is not null && _state?.CanFinish == true => _finishImpact,
            FocusTarget.FinishButton when _finishConfirmation is null && _state?.CanFinish == true => _finishButton,
            FocusTarget.Screenshot when _state?.CanTakeScreenshot == true => _screenshotButton,
            FocusTarget.RangeStatus when _state is { RangeStart: not null, PendingRange: null } => _rangeStatus,
            FocusTarget.RangeStart when _state is { Recording: true, Paused: false, CanStartRange: true } && _rangeDraft is null => _startRangeButton,
            FocusTarget.RangeDraft when _rangeDraft is { Open: false } => _rangeDraftButton,
            FocusTarget.RangeEditor when _rangeDraft is { Open: true } => _rangeEditorHeading,
            FocusTarget.RangePicker when textVisible && !_textError && _textItems is { Count: > 0 } && _textRangeId is null => _pickRangeButton,
            FocusTarget.Ranges when _pane == Pane.Text => _rangesTab,
            FocusTarget.Classification when _pane == Pane.Text && _editItem?.Id == request.ItemId => _editClassification,
            FocusTarget.EditButton or FocusTarget.PathRow when rowVisible && _textRangeId is null && _editItem?.Id != request.ItemId && row is not null => row.Open,
            FocusTarget.Gallery when _pane == Pane.Text && _galleryItem is not null => _galleryHeading,
            FocusTarget.GalleryButton when rowVisible && _editItem?.Id != request.ItemId && row is not null => row.Open,
            FocusTarget.EditButton or FocusTarget.GalleryButton or FocusTarget.Text when textVisible => _textScroller,
            _ => default
        };
        if (destination.Id is not null)
        {
            try { await destination.FocusAsync(preventScroll: request.Target is FocusTarget.Screenshot or FocusTarget.PathRow or FocusTarget.FinishButton or FocusTarget.GalleryButton); }
            catch (JSDisconnectedException) { }
        }
    }

    private void FocusAfterRender(FocusTarget target, long itemId = 0)
    {
        // A status refresh during another action can also close the finish confirmation.
        if (_nextFocus?.Target == FocusTarget.FinishButton && target != FocusTarget.FinishImpact) return;
        _nextFocus = (target, itemId);
    }

    private void OpenFinishConfirmation(DisplayTarget target, PlayRecordingState displayed)
    {
        if (ControlsDisabled || _finishConfirmation is not null || !displayed.CanFinish || !IsDisplayedTargetCurrent(target)) return;
        _finishConfirmation = new(displayed.Title, displayed.RangeStart, displayed.PendingRange, _rangeDraft);
        FocusAfterRender(FocusTarget.FinishImpact);
    }

    private void CloseFinishConfirmation()
    {
        _finishConfirmation = null;
        FocusAfterRender(FocusTarget.FinishButton);
    }

    private void HandleEscape(DisplayTarget target, KeyboardEventArgs args, Action close)
    {
        if (args.Key == "Escape") EditWorkspace(target, close);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    var connectionVersion = ObserveConnection();
                    if (!WebSession.IsConnectionCurrent(connectionVersion)) return;
                    var before = (_state, _statusLoaded, _statusError, _textItems, _textError, _workspace, _rangeDraft, _latest, _imageCounts, _imageCountsFailed);
                    _pollRead = RefreshStatusAsync(loadPane: _statusError, statusOnly: _busy);
                    try { await _pollRead; }
                    catch (DisplayChangedException) { }
                    finally { _pollRead = null; }
                    if (!_disposed && before != (_state, _statusLoaded, _statusError, _textItems, _textError, _workspace, _rangeDraft, _latest, _imageCounts, _imageCountsFailed)) StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (InvalidOperationException) when (_disposed) { }
    }

    private Task RefreshAsync() => RunForegroundAsync(() => RefreshStatusAsync(loadPane: true), allowStale: true);

    private bool IsWorkspaceCurrent(DisplayTarget target) => !_disposed
        && ReferenceEquals(target.Workspace, _workspace) && WebSession.IsConnectionCurrent(target.ConnectionVersion);

    private bool IsDisplayedTargetCurrent(DisplayTarget target) => IsWorkspaceCurrent(target) && target.Recording == _state?.Target;

    private void EditWorkspace(DisplayTarget target, Action operation)
    {
        if (!ControlsDisabled && IsWorkspaceCurrent(target)) operation();
    }

    private Task ReadForDisplayedTargetAsync(DisplayTarget target, Func<Task> operation) =>
        RunForegroundAsync(operation, origin: target);

    private Task RunMutationAsync(DisplayTarget target, Func<Task> operation, string failureLabel, string? rejection = null,
        Func<bool>? isStillRelevant = null)
    {
        return RunForegroundAsync(async () =>
        {
            if (!IsWorkspaceCurrent(target) || isStillRelevant?.Invoke() == false) return;
            if (!IsDisplayedTargetCurrent(target))
            {
                await RefreshStatusAsync(loadPane: true);
                if (IsWorkspaceCurrent(target) && isStillRelevant?.Invoke() != false)
                    _actionError = new(failureLabel, "接続またはPCの記録状態が変わりました。現在の対象を確認してください。");
                return;
            }
            try { await operation(); }
            catch (InvalidOperationException ex)
            {
                Logger.LogWarning("Play recording Web operation rejected ({ExceptionType}).", ex.GetType().Name);
                await RefreshStatusAsync(loadPane: false);
                if (IsWorkspaceCurrent(target) && isStillRelevant?.Invoke() != false)
                    _actionError = new(failureLabel, rejection ?? "操作を続けられませんでした。現在の記録状態を確認してください。");
            }
        }, failureLabel: failureLabel, origin: target);
    }

    private async Task RunForegroundAsync(Func<Task> operation, bool allowStale = false, string failureLabel = "操作失敗", DisplayTarget? origin = null)
    {
        if (_busy || _disposed || _statusError && !allowStale) return;
        var connectionVersion = ObserveConnection();
        if (!WebSession.IsConnectionCurrent(connectionVersion) || origin is { } start && !IsWorkspaceCurrent(start)) return;
        _busy = true;
        _actionError = null;
        _actionSuccess = null;
        try
        {
            if (_pollRead is { } read)
            {
                try { await read; }
                catch (DisplayChangedException) { }
            }
            if (!_disposed && WebSession.IsConnectionCurrent(connectionVersion)
                && (!_statusError || allowStale) && (origin is null || IsWorkspaceCurrent(origin.Value))) await operation();
        }
        catch (DisplayChangedException) { }
        catch (Exception ex)
        {
            if (!_disposed && WebSession.IsConnectionCurrent(connectionVersion)
                && (origin is null || IsWorkspaceCurrent(origin.Value)))
            {
                Logger.LogWarning("Play recording Web operation failed ({ExceptionType}).", ex.GetType().Name);
                _actionError = new(failureLabel, "操作結果を確認できませんでした。現在の記録を確認してください。");
            }
        }
        finally
        {
            _busy = false;
            if (!_disposed)
            {
                ObserveConnection();
                StateHasChanged();
            }
        }
    }

    private long ObserveConnection()
    {
        var connectionVersion = WebSession.ConnectionVersion;
        if (_displayedConnectionVersion == connectionVersion) return connectionVersion;
        _displayedConnectionVersion = connectionVersion;
        ClearTargetData();
        _statusLoaded = _statusError = false;
        if (!_disposed) StateHasChanged();
        return connectionVersion;
    }

    private Task SelectPaneAsync(DisplayTarget target, Pane pane) => ReadForDisplayedTargetAsync(target, async () =>
    {
        if (pane == _pane) return;
        // Keep the text surface mounted, and stop updates while the flow is in use.
        if (_pane == Pane.Text)
        {
            StopFollowing();
            if (_galleryItem is null) await RememberReadingPositionAsync(target);
        }
        if (!IsWorkspaceCurrent(target)) return;
        _pane = pane;
        if (pane == Pane.Text && _galleryItem is null) _restoreReadingPosition = _readingPosition;
        if (pane == Pane.Paths ? _graph is null || _pathsError || _pathsLoading : _textItems is null || _textError)
            await LoadPaneAsync();
    });

    private Task LoadPaneAsync() => _pane switch
    {
        Pane.Text => LoadTextAsync(),
        Pane.Paths => LoadPathsAsync(),
        _ => throw new InvalidOperationException()
    };

    /// <summary>Captures the Web text viewport's position before leaving it or opening its detail.</summary>
    private async Task RememberReadingPositionAsync(DisplayTarget target)
    {
        if (_textScroller.Id is null) return;
        try
        {
            var position = await Js.InvokeAsync<double>("playRecordingText.position", _textScroller);
            if (IsWorkspaceCurrent(target)) _readingPosition = position;
        }
        catch (JSDisconnectedException) { }
    }

    /// <summary>Keeps saved ranges and legacy scenes in a secondary section of the text surface.</summary>
    private Task ToggleSavedRangesAsync(DisplayTarget target) => ReadForDisplayedTargetAsync(target, async () =>
    {
        _savedRangesOpen = !_savedRangesOpen;
        if (_savedRangesOpen) await LoadRangesAsync();
    });

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        ClearTargetData();
        return ValueTask.CompletedTask;
    }

    private static string ClassificationName(CaptureKind? kind, string? other = null) => kind switch
    {
        CaptureKind.Dialogue => "セリフ",
        CaptureKind.Narration => "地の文",
        CaptureKind.Other => other ?? "未判定",
        _ => "未判定"
    };

    /// <summary>Shows only the current record label, never another branch selection.</summary>
    private static string RecordingRootLabel(PlayRecordingState state)
    {
        const string heads = "refs/heads/";
        return state.RecordingBranchRef switch
        {
            { } branch when branch.StartsWith(heads, StringComparison.Ordinal) => branch[heads.Length..],
            { } branch => branch,
            _ => "作品の記録"
        };
    }

}
