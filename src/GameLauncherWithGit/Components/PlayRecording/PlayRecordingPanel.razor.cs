using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Components.PlayRecording;

/// <summary>Record controls for a Web circuit already authorized by the host.</summary>
public partial class PlayRecordingPanel
{
    [Inject] private ILogger<PlayRecordingPanel> Logger { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    private enum Pane { Text, Ranges, Paths, Scenes }

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _pollRead;
    private long _displayedConnectionVersion;
    private readonly record struct DisplayTarget(PlayRecordingTarget Recording, long ConnectionVersion);
    private DisplayTarget ForDisplay(PlayRecordingTarget target) => new(target, _displayedConnectionVersion);
    private bool _busy, _disposed, _statusError, _unauthorized;
    private string? _gameTitle, _notice, _actionError;
    private Pane _pane;

    private bool ControlsDisabled => _busy || _statusError || _unauthorized || !HasUsableTarget;
    private static string Bool(bool value) => value ? "true" : "false";
    private string Pressed(Pane pane) => Bool(_pane == pane);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Prerender must not read from or authenticate an Interactive Server circuit.
        if (!firstRender) return;
        await RefreshAsync();
        if (!_disposed && !_unauthorized) _ = PollAsync(_lifetime.Token);
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

    private Task RunMutationAsync(DisplayTarget target, Func<Task> operation, string? rejection = null)
    {
        return RunForegroundAsync(async () =>
        {
            if (!IsDisplayedTargetCurrent(target))
            {
                await RefreshStatusAsync(loadPane: true);
                if (!_disposed && !_unauthorized)
                    _actionError = "接続またはPCの記録状態が変わりました。現在の対象を確認してください。";
                return;
            }
            try { await operation(); }
            catch (InvalidOperationException ex)
            {
                Logger.LogWarning("Play recording Web operation rejected ({ExceptionType}).", ex.GetType().Name);
                await RefreshStatusAsync(loadPane: false);
                if (!_disposed && !_unauthorized)
                    _actionError = rejection ?? "操作を続けられませんでした。現在の記録状態を確認してください。";
            }
        });
    }

    private async Task RunForegroundAsync(Func<Task> operation, bool allowStale = false)
    {
        if (_busy || _unauthorized || _disposed || _statusError && !allowStale) return;
        _busy = true;
        _actionError = _notice = null;
        try
        {
            if (_pollRead is { } read) await read;
            if (!_unauthorized && !_disposed && (!_statusError || allowStale)) await operation();
        }
        catch (UnauthorizedAccessException) { ExpireAccess(); }
        catch (Exception ex)
        {
            if (!_unauthorized && !_disposed)
            {
                Logger.LogWarning("Play recording Web operation failed ({ExceptionType}).", ex.GetType().Name);
                _actionError = "操作結果を確認できませんでした。現在の記録を確認してください。";
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
