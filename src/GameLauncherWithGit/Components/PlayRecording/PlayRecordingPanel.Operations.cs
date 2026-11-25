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
    // The command version can change without changing which recording the user is reading.
    private (long? ProfileId, long? SessionId, long ConnectionVersion) WorkspaceKey =>
        (_state?.Target.ProfileId, _state?.Target.SessionId, _displayedConnectionVersion);

    private string RecordingStatus => _state switch
    {
        { BlockedByOtherGame: true } => "PCで別のゲームを記録中",
        { Paused: true } => "一時停止中",
        { Recording: true } => "記録中",
        { CanStart: true } => "記録停止中",
        _ => "記録を開始できません"
    };

    private sealed record RangeDraft(PlayRecordingRangeSelection Selection, bool Shared = false, bool AutoFocus = false,
        CaptureTextItem? First = null, CaptureTextItem? Last = null);

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
            if (_disposed || _unauthorized || !WebSession.IsConnectionCurrent(connectionVersion)) return;
            Logger.LogWarning("Play recording Web read failed for {Section} ({ExceptionType}).", section, ex.GetType().Name);
            failed();
        }
    }

    private async Task RefreshStatusAsync(bool loadPane, bool refreshLatestText = true)
    {
        var connectionVersion = WebSession.ConnectionVersion;
        PlayRecordingState state;
        try { state = await UseAsync(Operations.GetStateAsync); }
        catch (UnauthorizedAccessException) { ExpireAccess(); return; }
        catch (Exception ex)
        {
            if (_disposed || !WebSession.IsConnectionCurrent(connectionVersion)) return;
            if (!_statusError)
                Logger.LogWarning("Play recording Web status unavailable ({ExceptionType}).", ex.GetType().Name);
            if (_displayedConnectionVersion != connectionVersion)
            {
                ClearTargetData();
                _gameTitle = null;
                _displayedConnectionVersion = connectionVersion;
            }
            _statusError = true;
            return;
        }
        if (_disposed || _unauthorized || !WebSession.IsConnectionCurrent(connectionVersion)) return;

        var targetChanged = _state?.Target.ProfileId != state.Target.ProfileId ||
            _state?.Target.SessionId != state.Target.SessionId || _state?.BlockedByOtherGame != state.BlockedByOtherGame
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
                _rangeDraft = new(pending, Shared: true, AutoFocus: _focusNextSharedRange);
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
        else if (refreshLatestText && _pane == Pane.Text && _latest && _textRangeId is null && _olderPages.Count == 0 && !_pickingRange && _editItem is null && _galleryItem is null)
            await RefreshLatestTextAsync();
    }

    private void ClearTargetData()
    {
        _state = null;
        _actionError = null;
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
    }, "区間開始失敗");

    private Task EndRangeAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseAsync(target, gameId => Operations.EndRangeAsync(gameId, target.Recording));
        _focusNextSharedRange = true;
        try { await RefreshStatusAsync(loadPane: false); }
        finally { _focusNextSharedRange = false; }
    }, "区間固定失敗");

    private Task CancelRangeAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, gameId => Operations.CancelRangeAsync(gameId, target.Recording));
        _rangeDraft = null;
        await RefreshStatusAsync(loadPane: false);
        FocusAfterRender(FocusTarget.RangeStart);
    }, "区間取消失敗");

    private Task CloseRangeEditorAsync(DisplayTarget target, RangeDraft draft)
    {
        if (ControlsDisabled || !IsDisplayedTargetCurrent(target) || !ReferenceEquals(draft, _rangeDraft)) return Task.CompletedTask;
        if (draft.Shared) return CancelRangeAsync(target);
        _rangeDraft = null;
        FocusAfterRender(FocusTarget.RangePicker);
        return Task.CompletedTask;
    }

    private Task PrepareTextRangeAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        if (!TextRangeValid || _first is not { } first || _last is not { } last) return;
        var selection = await UseAsync(target, gameId => Operations.SelectRangeAsync(gameId, target.Recording, first, last));
        if (_disposed || _unauthorized) return;
        _rangeDraft = new(selection, AutoFocus: true, First: first, Last: last);
        ClearRangePick();
        await LoadRangesAsync();
    }, "区間指定失敗");

    private Task SaveRangeAsync(DisplayTarget target, RangeDraft draft, (string Name, string[] Tags) data) => RunMutationAsync(target, async () =>
    {
        if (!ReferenceEquals(draft, _rangeDraft)) return;
        var selection = draft.Selection;
        // A new Save can revalidate the original displayed endpoints; an old command is still rejected above.
        if (!draft.Shared && selection.Target != target.Recording && draft.First is { } first && draft.Last is { } last)
            selection = await UseAsync(target, gameId => Operations.SelectRangeAsync(gameId, target.Recording, first, last));
        await UseAsync(target, gameId => Operations.SaveRangeAsync(gameId, target.Recording, selection, data.Name, data.Tags));

        if (_disposed || _unauthorized) return;
        _rangeDraft = null;
        _pane = Pane.Ranges;
        await RefreshStatusAsync(loadPane: true);
        if (!_disposed && !_unauthorized)
            FocusAfterRender(FocusTarget.Ranges);
    }, "区間保存失敗");

    private Task StartRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.StartAsync(gameId, target.Recording),
            "記録開始失敗", "記録を開始できませんでした。PCで対象ゲームと取得設定を確認してください。");

    private Task PauseRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.PauseAsync(gameId, target.Recording), "一時停止失敗", focus: FocusTarget.Resume);

    private Task ResumeRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.ResumeAsync(gameId, target.Recording), "記録再開失敗", focus: FocusTarget.Pause);

    private Task FinishRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.FinishAsync(gameId, target.Recording), "記録終了失敗");

    private Task ChangeRecordingAsync(DisplayTarget target, Func<string, Task> operation, string failureLabel, string? rejection = null, FocusTarget? focus = null) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, operation);
        _confirmFinish = false;
        await RefreshStatusAsync(loadPane: true);
        if (focus is { } next) FocusAfterRender(next);
    }, failureLabel, rejection);

    private Task BreakLivePathAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, gameId => Operations.BreakPathAsync(gameId, target.Recording));
        await RefreshStatusAsync(loadPane: true);
    }, "経路変更失敗");

    private async Task TakeScreenshotAsync(DisplayTarget target)
    {
        bool StillShowingRecording() => HasUsableTarget && !_statusError
            && WebSession.IsConnectionCurrent(target.ConnectionVersion)
            && _state!.Target.ProfileId == target.Recording.ProfileId
            && _state.Target.SessionId == target.Recording.SessionId;

        await RunMutationAsync(target, async () =>
        {
            ClearScreenshotResult();
            var shot = await UseAsync(target, gameId => Operations.TakeScreenshotAsync(gameId, target.Recording));
            await RefreshStatusAsync(loadPane: false, refreshLatestText: false);
            if (StillShowingRecording() && _state?.Target.SessionId == shot.Event.SessionId)
                _screenshotResult = shot;
        }, "撮影失敗", "ゲーム画像を保存できませんでした。PCで対象のゲームを確認してください。");

        if (StillShowingRecording()) FocusAfterRender(FocusTarget.Screenshot);
    }
}
