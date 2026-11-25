using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace GameLauncherWithGit.Components.PlayRecording;

/// <summary>Record controls for a Web circuit already authorized by the host.</summary>
public partial class PlayRecordingPanel
{
    [Inject] private ILogger<PlayRecordingPanel> Logger { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private enum Pane { Text, Ranges, Paths, Scenes }
    private enum FocusTarget { FinishImpact, FinishButton, Pause, Resume, Screenshot, RangeStatus, RangeStart, RangePicker, Ranges, Classification, EditButton, PathRow, Gallery, GalleryButton, Text }
    private (FocusTarget Target, long ItemId)? _nextFocus;
    private ElementReference _finishImpact, _finishButton, _pauseButton, _resumeButton, _rangeStatus,
        _startRangeButton, _pickRangeButton, _rangesTab, _editClassification, _galleryHeading, _textHeading, _screenshotButton;
    private sealed class RowButtons { public ElementReference Open; }
    private readonly Dictionary<long, RowButtons> _rowButtons = [];

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _pollRead;
    private long _displayedConnectionVersion;
    private readonly record struct DisplayTarget(PlayRecordingTarget Recording, long ConnectionVersion);
    private DisplayTarget ForDisplay(PlayRecordingTarget target) => new(target, _displayedConnectionVersion);
    private bool _busy, _disposed, _statusError, _unauthorized;
    private string? _gameTitle;
    private sealed record OperationFailure(string Label, string Detail);
    private OperationFailure? _actionError;
    private Pane _pane;

    private bool ControlsDisabled => _busy || _statusError || _unauthorized || !HasUsableTarget;
    private static string Bool(bool value) => value ? "true" : "false";
    private string Pressed(Pane pane) => Bool(_pane == pane);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Prerender must not read from or authenticate an Interactive Server circuit.
        if (firstRender)
        {
            await RefreshAsync();
            if (!_disposed && !_unauthorized) _ = PollAsync(_lifetime.Token);
            return;
        }
        if (_nextFocus is not { } request || _busy) return;
        _nextFocus = null;
        if (ControlsDisabled || _disposed) return;

        var textVisible = _pane == Pane.Text && _galleryItem is null;
        var rowVisible = textVisible && !_pickingRange && !_textError;
        var row = _rowButtons.GetValueOrDefault(request.ItemId);
        var destination = request.Target switch
        {
            FocusTarget.FinishImpact when _finishConfirmation is not null && _state?.CanFinish == true => _finishImpact,
            FocusTarget.FinishButton when _finishConfirmation is null && _state?.CanFinish == true => _finishButton,
            FocusTarget.Pause when _state is { Paused: false, Recording: true } => _pauseButton,
            FocusTarget.Resume when _state?.Paused == true => _resumeButton,
            FocusTarget.Screenshot when _state?.CanTakeScreenshot == true => _screenshotButton,
            FocusTarget.RangeStatus when _state is { RangeStart: not null, PendingRange: null } => _rangeStatus,
            FocusTarget.RangeStart when _state is { Recording: true, Paused: false, CanStartRange: true } && _rangeDraft is null => _startRangeButton,
            FocusTarget.RangePicker when textVisible && !_textError && _textItems is { Count: > 0 } && _textRangeId is null => _pickRangeButton,
            FocusTarget.Ranges when _pane == Pane.Ranges => _rangesTab,
            FocusTarget.Classification when rowVisible && _editItem?.Id == request.ItemId => _editClassification,
            FocusTarget.EditButton or FocusTarget.PathRow when rowVisible && _textRangeId is null && _editItem?.Id != request.ItemId && row is not null => row.Open,
            FocusTarget.Gallery when _pane == Pane.Text && _galleryItem?.Id == request.ItemId => _galleryHeading,
            FocusTarget.GalleryButton when rowVisible && _editItem?.Id != request.ItemId && row is not null => row.Open,
            FocusTarget.EditButton or FocusTarget.GalleryButton or FocusTarget.Text when textVisible => _textHeading,
            _ => default
        };
        if (destination.Id is not null)
        {
            try { await destination.FocusAsync(preventScroll: request.Target is FocusTarget.Screenshot or FocusTarget.PathRow or FocusTarget.FinishButton); }
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
        _finishConfirmation = new(target, displayed.Title, displayed.RangeStart, displayed.PendingRange);
        FocusAfterRender(FocusTarget.FinishImpact);
    }

    private void CloseFinishConfirmation()
    {
        _finishConfirmation = null;
        FocusAfterRender(FocusTarget.FinishButton);
    }

    private void HandleEscape(KeyboardEventArgs args, Action close)
    {
        if (args.Key == "Escape" && !ControlsDisabled) close();
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
                    if (_busy || _unauthorized || _disposed || !WebSession.IsConnectionCurrent(WebSession.ConnectionVersion)) return;
                    var before = (_state, _statusError, _textItems, _textError, _unauthorized, _displayedConnectionVersion);
                    _pollRead = RefreshStatusAsync(loadPane: _gameTitle is null || _statusError);
                    try { await _pollRead; }
                    finally { _pollRead = null; }
                    if (!_disposed && before != (_state, _statusError, _textItems, _textError, _unauthorized, _displayedConnectionVersion)) StateHasChanged();
                });
                if (_unauthorized) break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (InvalidOperationException) when (_disposed) { }
    }

    private Task RefreshAsync() => RunForegroundAsync(() => RefreshStatusAsync(loadPane: true), allowStale: true);
    private void ReloadPage() => Navigation.NavigateTo("/", forceLoad: true);

    private bool IsDisplayedTargetCurrent(DisplayTarget target) => target.Recording == _state?.Target
        && _displayedConnectionVersion == target.ConnectionVersion && WebSession.IsConnectionCurrent(target.ConnectionVersion);

    private Task ReadForDisplayedTargetAsync(DisplayTarget target, Func<Task> operation) =>
        RunForegroundAsync(() => IsDisplayedTargetCurrent(target) ? operation() : Task.CompletedTask);

    private Task RunMutationAsync(DisplayTarget target, Func<Task> operation, string failureLabel, string? rejection = null,
        Func<bool>? isStillRelevant = null)
    {
        return RunForegroundAsync(async () =>
        {
            if (isStillRelevant?.Invoke() == false) return;
            if (!IsDisplayedTargetCurrent(target))
            {
                await RefreshStatusAsync(loadPane: true);
                if (!_disposed && !_unauthorized && WebSession.IsConnectionCurrent(target.ConnectionVersion) &&
                    isStillRelevant?.Invoke() != false)
                    _actionError = new(failureLabel, "接続またはPCの記録状態が変わりました。現在の対象を確認してください。");
                return;
            }
            try { await operation(); }
            catch (InvalidOperationException ex)
            {
                Logger.LogWarning("Play recording Web operation rejected ({ExceptionType}).", ex.GetType().Name);
                await RefreshStatusAsync(loadPane: false);
                if (!_disposed && !_unauthorized && WebSession.IsConnectionCurrent(target.ConnectionVersion) &&
                    isStillRelevant?.Invoke() != false)
                    _actionError = new(failureLabel, rejection ?? "操作を続けられませんでした。現在の記録状態を確認してください。");
            }
        }, failureLabel: failureLabel);
    }

    private async Task RunForegroundAsync(Func<Task> operation, bool allowStale = false, string failureLabel = "操作失敗")
    {
        if (_busy || _unauthorized || _disposed || _statusError && !allowStale) return;
        _busy = true;
        _actionError = null;
        var connectionVersion = WebSession.ConnectionVersion;
        try
        {
            if (_pollRead is { } read) await read;
            if (!_unauthorized && !_disposed && (!_statusError || allowStale)) await operation();
        }
        catch (UnauthorizedAccessException) { ExpireAccess(); }
        catch (Exception ex)
        {
            if (!_unauthorized && !_disposed && WebSession.IsConnectionCurrent(connectionVersion))
            {
                Logger.LogWarning("Play recording Web operation failed ({ExceptionType}).", ex.GetType().Name);
                _actionError = new(failureLabel, "操作結果を確認できませんでした。現在の記録を確認してください。");
            }
        }
        finally
        {
            _busy = false;
            if (!_disposed) StateHasChanged();
        }
    }

    private void ExpireAccess()
    {
        _unauthorized = true;
        _gameTitle = null;
        ClearTargetData();
    }

    private Task SelectPaneAsync(Pane pane) => RunForegroundAsync(async () =>
    {
        _pane = pane;
        CloseGallery();
        CloseTextEditor();
        if (pane != Pane.Text) ClearRangePick();
        await LoadPaneAsync();
    });

    private Task LoadPaneAsync() => _pane switch
    {
        Pane.Text => LoadTextAsync(),
        Pane.Ranges => LoadRangesAsync(),
        Pane.Paths => LoadPathsAsync(),
        Pane.Scenes => LoadScenesAsync(),
        _ => throw new InvalidOperationException()
    };

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

    private static string SceneName(ScenarioSceneEdit scene) => string.IsNullOrWhiteSpace(scene.DisplayNumber)
        ? scene.Title
        : $"{scene.DisplayNumber} {scene.Title}";
}
