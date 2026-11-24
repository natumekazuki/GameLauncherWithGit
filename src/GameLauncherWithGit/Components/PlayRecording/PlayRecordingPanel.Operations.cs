using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.PlayRecordingWeb;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Components.PlayRecording;

public partial class PlayRecordingPanel
{
    [Inject] private PlayRecordingWebSession WebSession { get; set; } = default!;
    [Inject] private PlayRecordingOperations Operations { get; set; } = default!;

    private PlayRecordingState? _state;
    private RangeDraft? _rangeDraft;
    private bool _confirmFinish;
    private bool _focusNextSharedRange;
    private bool HasUsableTarget => _state is { BlockedByOtherGame: false };

    private string RecordingStatus => _state switch
    {
        { BlockedByOtherGame: true } => "PCで別のゲームを記録中",
        { Paused: true } => "一時停止中",
        { Recording: true } => "記録中",
        { CanStart: true } => "記録停止中",
        _ => "記録を開始できません"
    };

    private sealed record RangeDraft(DisplayTarget Target, PlayRecordingRangeSelection Selection, bool Shared = false, bool AutoFocus = false);

    private Task<T> UseAsync<T>(Func<string, Task<T>> operation) => WebSession.UseAsync(operation);

    private Task<T> UseAsync<T>(DisplayTarget target, Func<string, Task<T>> operation) =>
        WebSession.UseAsync(target.ConnectionVersion, operation);

    private Task UseActionAsync(DisplayTarget target, Func<string, Task> operation) => UseAsync(target, async gameId =>
    {
        await operation(gameId);
        return true;
    });

    private async Task ReadAsync<T>(string section, Func<string, Task<T>> read, Action<T> apply, Action failed)
    {
        if (_disposed || _unauthorized) return;
        var connectionVersion = WebSession.ConnectionVersion;
        try
        {
            var value = await UseAsync(read);
            if (!_disposed && !_unauthorized && WebSession.IsConnectionCurrent(connectionVersion)) apply(value);
        }
        catch (UnauthorizedAccessException) { ExpireAccess(); }
        catch (Exception ex)
        {
            if (_disposed || _unauthorized) return;
            Logger.LogWarning("Play recording Web read failed for {Section} ({ExceptionType}).", section, ex.GetType().Name);
            failed();
        }
    }

    private async Task RefreshStatusAsync(bool loadPane)
    {
        var connectionVersion = WebSession.ConnectionVersion;
        PlayRecordingState state;
        try { state = await UseAsync(Operations.GetStateAsync); }
        catch (UnauthorizedAccessException) { ExpireAccess(); return; }
        catch (Exception ex)
        {
            if (!_statusError && !_disposed)
                Logger.LogWarning("Play recording Web status unavailable ({ExceptionType}).", ex.GetType().Name);
            _statusError = true;
            return;
        }
        if (_disposed || _unauthorized || !WebSession.IsConnectionCurrent(connectionVersion)) return;

        var targetChanged = _state?.Target != state.Target || _state?.BlockedByOtherGame != state.BlockedByOtherGame
            || _displayedConnectionVersion != connectionVersion;
        if (targetChanged) ClearTargetData();
        _displayedConnectionVersion = connectionVersion;
        _state = state;
        _gameTitle = state.Title;
        _statusError = false;
        if (!HasUsableTarget) return;

        if (state.PendingRange is { } pending)
        {
            if (_rangeDraft is null || _rangeDraft.Shared && !Equals(_rangeDraft.Selection, pending))
            {
                _rangeDraft = new(ForDisplay(state.Target), pending, Shared: true, AutoFocus: _focusNextSharedRange);
                await LoadRangesAsync();
            }
        }
        else if (_rangeDraft?.Shared == true)
        {
            _rangeDraft = null;
            if (_pane == Pane.Ranges) await LoadRangesAsync();
        }

        if (_disposed || _unauthorized) return;
        if (loadPane || targetChanged) await LoadPaneAsync();
        else if (_pane == Pane.Text && _latest && _textRangeId is null && _olderPages.Count == 0 && !_pickingRange && _editItem is null && _galleryItem is null)
            await RefreshLatestTextAsync();
    }

    private void ClearTargetData()
    {
        _state = null;
        _notice = _actionError = null;
        _rangeDraft = null;
        _confirmFinish = false;
        _latest = true;
        ResetTextPages();
        _textRangeId = null;
        _textItems = null;
        _rowButtons.Clear();
        _nextFocus = null;
        _ranges = null;
        _labels = [];
        _rangeTags = _speakers = [];
        _textError = _rangesError = _pathsError = _scenesError = false;
        ClearRangePick();
        CloseTextEditor();
        CloseGallery();
        _graph = null;
        _sceneFlow = null;
        _sceneDraft = null;
        _connectionTarget = 0;
        ClearScreenshotResult();
    }

    private async Task LoadRangesAsync()
    {
        if (_state is not { } state) return;
        await ReadAsync("ranges", gameId => Operations.GetRangesAsync(gameId, state.Target), ranges =>
            {
                _ranges = ranges;
                _rangeTags = ranges.SelectMany(x => x.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                _rangesError = false;
            }, () => _rangesError = true);
        if (_rangeDraft is not null)
            await ReadAsync("speakers", gameId => Operations.GetSpeakersAsync(gameId, state.Target), speakers => _speakers = speakers, () => _speakers = []);
    }

    private Task StartRangeAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, gameId => Operations.StartRangeAsync(gameId, target.Recording));
        await RefreshStatusAsync(loadPane: false);
        FocusAfterRender(FocusTarget.RangeStatus);
    });

    private Task EndRangeAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseAsync(target, gameId => Operations.EndRangeAsync(gameId, target.Recording));
        _focusNextSharedRange = true;
        try { await RefreshStatusAsync(loadPane: false); }
        finally { _focusNextSharedRange = false; }
    });

    private Task CancelRangeAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, gameId => Operations.CancelRangeAsync(gameId, target.Recording));
        _rangeDraft = null;
        await RefreshStatusAsync(loadPane: false);
        FocusAfterRender(FocusTarget.RangeStart);
    });

    private Task CloseRangeEditorAsync(RangeDraft draft)
    {
        if (ControlsDisabled || !IsDisplayedTargetCurrent(draft.Target) || !ReferenceEquals(draft, _rangeDraft)) return Task.CompletedTask;
        if (draft.Shared) return CancelRangeAsync(draft.Target);
        _rangeDraft = null;
        FocusAfterRender(FocusTarget.RangePicker);
        return Task.CompletedTask;
    }

    private Task PrepareTextRangeAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        if (!TextRangeValid || _first is not { } first || _last is not { } last) return;
        var selection = await UseAsync(target, gameId => Operations.SelectRangeAsync(gameId, target.Recording, first, last));
        if (_disposed || _unauthorized) return;
        _rangeDraft = new(target, selection, AutoFocus: true);
        ClearRangePick();
        await LoadRangesAsync();
    });

    private Task SaveRangeAsync(RangeDraft draft, (string Name, string[] Tags) data) => RunMutationAsync(draft.Target, async () =>
    {
        if (!ReferenceEquals(draft, _rangeDraft)) return;
        await UseAsync(draft.Target, gameId => Operations.SaveRangeAsync(gameId, draft.Target.Recording, draft.Selection, data.Name, data.Tags));

        if (_disposed || _unauthorized) return;
        _rangeDraft = null;
        _pane = Pane.Ranges;
        await RefreshStatusAsync(loadPane: true);
        if (!_disposed && !_unauthorized)
        {
            _notice = "区間を保存しました。";
            FocusAfterRender(FocusTarget.Ranges);
        }
    });

    private Task StartRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.StartAsync(gameId, target.Recording),
            "記録を開始できませんでした。PCで対象ゲームと取得設定を確認してください。");

    private Task PauseRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.PauseAsync(gameId, target.Recording), focus: FocusTarget.Resume);

    private Task ResumeRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.ResumeAsync(gameId, target.Recording), focus: FocusTarget.Pause);

    private Task FinishRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.FinishAsync(gameId, target.Recording));

    private Task ChangeRecordingAsync(DisplayTarget target, Func<string, Task> operation, string? rejection = null, FocusTarget? focus = null) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, operation);
        _confirmFinish = false;
        await RefreshStatusAsync(loadPane: true);
        if (focus is { } next) FocusAfterRender(next);
    }, rejection);

    private Task BreakLivePathAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, gameId => Operations.BreakPathAsync(gameId, target.Recording));
        await RefreshStatusAsync(loadPane: true);
        if (_disposed || _unauthorized) return;
        _notice = "次の本文から別経路にします。";
    });

    private Task TakeScreenshotAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        ClearScreenshotResult();
        var shot = await UseAsync(target, gameId => Operations.TakeScreenshotAsync(gameId, target.Recording));
        await RefreshStatusAsync(loadPane: true);
        if (HasUsableTarget && _state?.Target.SessionId == shot.Event.SessionId)
            _screenshotResult = shot;
    }, "ゲーム画像を保存できませんでした。PCで対象のゲームを確認してください。");
}
