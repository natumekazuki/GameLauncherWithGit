using System.Runtime.ExceptionServices;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Components;
using GameLauncherWithGit.Infrastructure.PlayRecordingWeb;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Components.PlayRecording;

public partial class PlayRecordingPanel
{
    [Inject] private PlayRecordingWebSession WebSession { get; set; } = default!;
    [Inject] private PlayRecordingOperations Operations { get; set; } = default!;

    private PlayRecordingState? _state;
    private long _nextStatusRead, _appliedStatusRead;
    private readonly record struct StatusRead(long Order, PlayRecordingState? State);
    private RangeDraft? _rangeDraft;
    private FinishConfirmation? _finishConfirmation;
    private bool _focusNextSharedRange;
    private bool _finishAfterRangeSave;
    private bool _waitingAfterResume;
    private readonly Dictionary<long, string> _rangeStartLabels = [];
    private ScenarioRecordingDisplay RecordingDisplay => _state switch
    {
        { Paused: true } => ScenarioRecordingDisplay.Paused,
        { Recording: true, CanTakeScreenshot: true } => ScenarioRecordingDisplay.Recording,
        { Recording: true } when _waitingAfterResume => ScenarioRecordingDisplay.WaitingAfterResume,
        { Recording: true } => ScenarioRecordingDisplay.WaitingForText,
        { CanStart: false, BlockedByOtherGame: false } => ScenarioRecordingDisplay.Unconfigured,
        _ => ScenarioRecordingDisplay.NotRecording
    };

    /// <summary>Owns one draft's values outside the shared form; closing the form leaves them intact.</summary>
    private sealed class RangeDraft(PlayRecordingRangeSelection selection, bool shared, bool open,
        CaptureTextItem? first = null, CaptureTextItem? last = null)
    {
        public PlayRecordingRangeSelection Selection { get; } = selection;
        public bool Shared { get; } = shared;
        public bool AutoFocus { get; } = open;
        public CaptureTextItem? First { get; } = first;
        public CaptureTextItem? Last { get; } = last;
        public bool Open { get; set; } = open;
        public string Name { get; set; } = "";
        public string[] Tags { get; set; } = [];
    }

    private sealed record FinishConfirmation(string Title, CapturePosition? RangeStart,
        PlayRecordingRangeSelection? PendingRange, RangeDraft? Draft)
    {
        public bool HasUnsavedRange => RangeStart is not null || PendingRange is not null || Draft is not null;

        // A new command version alone (for example, after a screenshot) does not change the impact.
        public bool Matches(PlayRecordingState state, RangeDraft? draft) =>
            state.CanFinish &&
            Title == state.Title && Equals(RangeStart, state.RangeStart) && ReferenceEquals(PendingRange, state.PendingRange)
            && ReferenceEquals(Draft, draft);
    }

    // A command version can change without replacing the workspace (pause, screenshot, etc.).
    private static bool SameWorkspace(PlayRecordingState? left, PlayRecordingState? right) =>
        left?.GameId == right?.GameId && left?.Target.ProfileId == right?.Target.ProfileId &&
        left?.Target.SessionId == right?.Target.SessionId && left?.Target.ScopeVersion == right?.Target.ScopeVersion;

    /// <summary>Reads the PC's current recording target only for a live browser connection.</summary>
    private async Task<StatusRead> GetVisibleStateAsync(long connectionVersion)
    {
        if (_disposed || !WebSession.IsConnectionCurrent(connectionVersion)) throw new DisplayChangedException();
        var order = ++_nextStatusRead;
        PlayRecordingState? state;
        try { state = await Operations.GetCurrentStateAsync(); }
        catch (Exception ex)
        {
            // An old or disconnected read must not overwrite the new workspace.
            if (!_disposed && WebSession.IsConnectionCurrent(connectionVersion) && order > _appliedStatusRead)
            {
                _appliedStatusRead = order;
                if (!_statusError)
                {
                    Logger.LogWarning("Play recording Web status unavailable ({ExceptionType}).", ex.GetType().Name);
                    // An unavailable status is not a confirmed target change. Hide and disable the view,
                    // but preserve its position and inputs until a successful read resolves the scope.
                    _statusError = true;
                    StateHasChanged();
                }
            }
            throw new DisplayChangedException();
        }
        var visible = state is not null && !string.IsNullOrEmpty(state.GameId) ? state : null;
        return new(order, visible);
    }

    /// <summary>Applies a Web operation only to its displayed recording target and connection generation.</summary>
    private async Task<T> UseAsync<T>(DisplayTarget target, Func<string, Task<T>> operation)
    {
        if (!IsDisplayedTargetCurrent(target)) throw new DisplayChangedException();

        // Operations validate this exact token against the PC under the desktop operation gate.
        T? value = default;
        ExceptionDispatchInfo? failure = null;
        try { value = await operation(target.GameId); }
        catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        if (!IsWorkspaceCurrent(target)) throw new DisplayChangedException();
        var current = await GetVisibleStateAsync(target.ConnectionVersion);
        ApplyStatus(current, target.ConnectionVersion);
        if (!IsWorkspaceCurrent(target)) throw new DisplayChangedException();
        failure?.Throw();
        return value!;
    }

    private Task UseActionAsync(DisplayTarget target, Func<string, Task> operation) => UseAsync(target, async gameId =>
    {
        await operation(gameId);
        return true;
    });

    private async Task ReadAsync<T>(DisplayTarget target, string section, Func<string, Task<T>> read, Action<T> apply, Action failed)
    {
        try
        {
            var value = await UseAsync(target, read);
            if (IsWorkspaceCurrent(target)) apply(value);
        }
        catch (Exception ex) when (ex is not DisplayChangedException)
        {
            if (!IsWorkspaceCurrent(target)) throw new DisplayChangedException();
            Logger.LogWarning("Play recording Web read failed for {Section} ({ExceptionType}).", section, ex.GetType().Name);
            failed();
        }
    }

    private async Task RefreshStatusAsync(bool loadPane, bool refreshLatestText = true, bool statusOnly = false)
    {
        var connectionVersion = ObserveConnection();
        if (!WebSession.IsConnectionCurrent(connectionVersion)) return;
        var current = await GetVisibleStateAsync(connectionVersion);
        ApplyStatus(current, connectionVersion);
        if (statusOnly || _state is not { } state) return;

        if (state.PendingRange is { } pending)
        {
            if (_rangeDraft is null || _rangeDraft.Shared && !ReferenceEquals(_rangeDraft.Selection, pending))
            {
                _rangeDraft = new(pending, shared: true, open: _focusNextSharedRange);
                _finishAfterRangeSave = false;
                await LoadRangesAsync();
            }
        }
        else if (_rangeDraft?.Shared == true)
        {
            _rangeDraft = null;
            _finishAfterRangeSave = false;
            if (_savedRangesOpen) await LoadRangesAsync();
        }

        if (loadPane || _paneNeedsLoad)
        {
            _paneNeedsLoad = true;
            await LoadPaneAsync();
            _paneNeedsLoad = false;
        }
        else if (refreshLatestText && _pane == Pane.Text && _latest && _textRangeId is null && _olderPages.Count == 0 && !_pickingRange && _editItem is null && _galleryItem is null)
            await RefreshLatestTextAsync();
    }

    private void ApplyStatus(StatusRead read, long connectionVersion)
    {
        if (_disposed || !WebSession.IsConnectionCurrent(connectionVersion)) throw new DisplayChangedException();
        // Status polling can finish while a foreground read is still in flight.
        if (read.Order <= _appliedStatusRead) return;
        _appliedStatusRead = read.Order;
        var state = read.State;
        var previous = _state;
        var changed = !SameWorkspace(previous, state);
        if (changed) ClearTargetData();
        // A pause/resume can be observed from the PC, not only from a Web click.
        if (state is not { Recording: true, CanTakeScreenshot: false }) _waitingAfterResume = false;
        else if (!changed && previous is { Paused: true } or { Recording: true, CanTakeScreenshot: true })
            _waitingAfterResume = true;
        _state = state;
        _statusLoaded = true;
        _statusError = false;
        if (_finishConfirmation is { } confirmation && state is not null && !confirmation.Matches(state, _rangeDraft)) CloseFinishConfirmation();
        if (changed)
        {
            _paneNeedsLoad = state is not null;
            // Remove the previous subtree before waiting for any content for the new target.
            StateHasChanged();
        }
    }

    private void ClearTargetData()
    {
        _workspace = new();
        _state = null;
        _pane = Pane.Text;
        _paneNeedsLoad = false;
        _focusNextSharedRange = false;
        _actionError = null;
        _actionSuccess = null;
        _rangeDraft = null;
        _finishConfirmation = null;
        _finishAfterRangeSave = false;
        _waitingAfterResume = false;
        _latest = true;
        _scrollToLatest = false;
        ResetTextPages();
        _textRangeId = null;
        _textSearchInput = _speakerSearchInput = "";
        _textSearch = _speakerSearch = null;
        _textItems = null;
        _imageCounts = null;
        _imageCountsFailed = false;
        _readingPosition = _restoreReadingPosition = null;
        _selectedItemId = null;
        _rowButtons.Clear();
        _nextFocus = null;
        _ranges = null;
        _rangeStartLabels.Clear();
        _labels = [];
        _rangeTags = _speakers = [];
        _textError = _rangesError = _pathsError = _scenesError = false;
        ClearRangePick();
        CloseTextEditor();
        CloseGallery();
        _graph = null;
        _matchingComplete = false;
        _sceneFlow = null;
        _savedScenesOpen = false;
        _savedRangesOpen = false;
        ClearScreenshotResult();
    }

    private Task LoadRangesAsync() => _state is { } state ? LoadRangesAsync(ForDisplay(state)) : Task.CompletedTask;

    private async Task LoadRangesAsync(DisplayTarget target)
    {
        await ReadAsync(target, "ranges", gameId => Operations.GetRangesAsync(gameId, target.Recording), ranges =>
            {
                _ranges = ranges;
                _rangeTags = ranges.SelectMany(x => x.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                _rangesError = false;
            }, () => _rangesError = true);
        // Only duplicate names need a distinguishing date; resolve their original start in this record.
        if (!_rangesError && _ranges is { } saved)
        {
            foreach (var range in saved.GroupBy(x => x.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).SelectMany(g => g))
            {
                if (_rangeStartLabels.ContainsKey(range.Id) || _state is not { } current || !IsWorkspaceCurrent(target)) continue;
                var recorded = ForDisplay(current);
                await ReadAsync(recorded, "range start", gameId => Operations.GetItemAtAsync(gameId, recorded.Recording, range.Start),
                    item => _rangeStartLabels[range.Id] = item.ReceivedAt.ToLocalTime().ToString("M/d HH:mm:ss"), () => { });
            }
        }
        if (_rangeDraft is not null)
            await ReadAsync(target, "speakers", gameId => Operations.GetSpeakersAsync(gameId, target.Recording), speakers => _speakers = speakers, () => _speakers = []);
    }

    private Task StartRangeAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, gameId => Operations.StartRangeAsync(gameId, target.Recording));
        await RefreshStatusAsync(loadPane: false);
        if (IsWorkspaceCurrent(target)) FocusAfterRender(FocusTarget.RangeStatus);
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
        if (_rangeDraft?.Shared == true) _rangeDraft = null;
        _finishAfterRangeSave = false;
        await RefreshStatusAsync(loadPane: false);
        if (IsWorkspaceCurrent(target)) FocusAfterRender(FocusTarget.RangeStart);
    }, "区間取消失敗");

    /// <summary>Hides the shared editor without changing the range, name, tags, or unsubmitted input.</summary>
    private void CloseRangeEditor(DisplayTarget target, RangeDraft draft)
    {
        if (ControlsDisabled || !IsDisplayedTargetCurrent(target) || !ReferenceEquals(draft, _rangeDraft)) return;
        draft.Open = false;
        _finishAfterRangeSave = false;
        FocusAfterRender(FocusTarget.RangeDraft);
    }

    /// <summary>Reopens the existing draft, without resolving or reselecting its endpoints.</summary>
    private void OpenRangeEditor(DisplayTarget target, RangeDraft draft)
    {
        if (!IsDisplayedTargetCurrent(target) || !ReferenceEquals(draft, _rangeDraft)) return;
        draft.Open = true;
        FocusAfterRender(FocusTarget.RangeEditor);
    }

    /// <summary>Only an explicit discard cancels a live range or drops a retrospective draft.</summary>
    private Task DiscardRangeEditorAsync(DisplayTarget target, RangeDraft draft)
    {
        if (ControlsDisabled || !IsDisplayedTargetCurrent(target) || !ReferenceEquals(draft, _rangeDraft)) return Task.CompletedTask;
        if (draft.Shared) return CancelRangeAsync(target);
        _rangeDraft = null;
        _finishAfterRangeSave = false;
        FocusAfterRender(FocusTarget.RangePicker);
        return Task.CompletedTask;
    }

    private Task PrepareTextRangeAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        if (!TextRangeValid || _first is not { } first || _last is not { } last) return;
        var selection = await UseAsync(target, gameId => Operations.SelectRangeAsync(gameId, target.Recording, first, last));
        if (_disposed) return;
        _rangeDraft = new(selection, shared: false, open: true, first, last);
        ClearRangePick();
        await LoadRangesAsync();
    }, "区間指定失敗");

    private Task SaveRangeAsync(DisplayTarget target, RangeDraft draft) => RunMutationAsync(target, async () =>
    {
        if (!ReferenceEquals(draft, _rangeDraft) || string.IsNullOrWhiteSpace(draft.Name)) return;
        var finishAfterSave = _finishAfterRangeSave;
        var selection = draft.Selection;
        // A new Save can revalidate the original displayed endpoints; an old command is still rejected above.
        if (!draft.Shared && selection.Target != target.Recording && draft.First is { } first && draft.Last is { } last)
            selection = await UseAsync(target, gameId => Operations.SelectRangeAsync(gameId, target.Recording, first, last));
        var currentTarget = ForDisplay(_state!);
        await UseAsync(currentTarget, gameId => Operations.SaveRangeAsync(gameId, currentTarget.Recording, selection, draft.Name.Trim(), draft.Tags));

        _rangeDraft = null;
        _finishAfterRangeSave = false;
        _actionSuccess = "区間を保存しました";
        // Saving changes the command version. Finish only the same session and only if no other draft appeared.
        if (finishAfterSave && _state is { CanFinish: true, RangeStart: null, PendingRange: null } finishedRange && IsWorkspaceCurrent(target))
        {
            var finishTarget = ForDisplay(finishedRange);
            try { await UseActionAsync(finishTarget, gameId => Operations.FinishAsync(gameId, finishTarget.Recording)); }
            catch (InvalidOperationException)
            {
                // The range was already saved; do not turn a failed finish into a failed save.
                _actionError = new("記録終了失敗", "区間は保存しました。現在の記録を確認して、もう一度終了してください。");
            }
        }
        if (_savedRangesOpen) await LoadRangesAsync();
        await RefreshStatusAsync(loadPane: false, refreshLatestText: false);
        if (IsWorkspaceCurrent(target))
            FocusAfterRender(FocusTarget.Ranges);
    }, "区間保存失敗");

    private Task StartRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.StartAsync(gameId, target.Recording),
            "記録開始失敗", "記録を開始できませんでした。PCで対象ゲームと取得設定を確認してください。");

    private Task PauseRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.PauseAsync(gameId, target.Recording), "一時停止失敗", focus: FocusTarget.Resume);

    private Task ResumeRecordingAsync(DisplayTarget target) =>
        ChangeRecordingAsync(target, gameId => Operations.ResumeAsync(gameId, target.Recording), "記録再開失敗", focus: FocusTarget.Pause);

    private Task FinishRecordingAsync(DisplayTarget target, FinishConfirmation confirmation)
    {
        if (!ReferenceEquals(_finishConfirmation, confirmation)) return Task.CompletedTask;
        return RunMutationAsync(target, async () =>
        {
            if (_state is not { } state || !confirmation.Matches(state, _rangeDraft))
            {
                CloseFinishConfirmation();
                return;
            }
            // The backend checks the rendered command version under the same gate as range changes.
            // Never substitute a newer version for this click.
            await UseActionAsync(target, gameId => Operations.FinishAsync(gameId, target.Recording));
            _finishConfirmation = null;
            await RefreshStatusAsync(loadPane: true);
        }, "記録終了失敗", isStillRelevant: () => ReferenceEquals(_finishConfirmation, confirmation));
    }

    /// <summary>Routes save-before-finish through the existing draft, or freezes a live range before naming it.</summary>
    private Task OpenFinishSaveAsync(DisplayTarget target, FinishConfirmation confirmation) => RunMutationAsync(target, async () =>
    {
        if (!ReferenceEquals(_finishConfirmation, confirmation) || _state is not { } state || !confirmation.Matches(state, _rangeDraft))
            return;
        if (_rangeDraft is null)
        {
            if (state.PendingRange is null && !state.CanEndRange) return;
            _focusNextSharedRange = true;
            try
            {
                if (state.PendingRange is null)
                    await UseAsync(target, gameId => Operations.EndRangeAsync(gameId, target.Recording));
                await RefreshStatusAsync(loadPane: false, refreshLatestText: false);
            }
            finally { _focusNextSharedRange = false; }
        }
        if (!IsWorkspaceCurrent(target) || _rangeDraft is not { } draft || _state is not { } current) return;
        _finishConfirmation = null;
        _nextFocus = null;
        _finishAfterRangeSave = true;
        OpenRangeEditor(ForDisplay(current), draft);
    }, "区間固定失敗");

    private Task ChangeRecordingAsync(DisplayTarget target, Func<string, Task> operation, string failureLabel, string? rejection = null, FocusTarget? focus = null) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, operation);
        await RefreshStatusAsync(loadPane: true);
        if (focus is { } next && IsWorkspaceCurrent(target)) FocusAfterRender(next);
    }, failureLabel, rejection);

    private Task BreakLivePathAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, gameId => Operations.BreakPathAsync(gameId, target.Recording));
        _actionSuccess = "次の本文から別経路";
        await RefreshStatusAsync(loadPane: _pane == Pane.Paths, refreshLatestText: false);
    }, "経路変更失敗");

    private async Task TakeScreenshotAsync(DisplayTarget target)
    {
        bool StillShowingRecording() => !_statusError && IsWorkspaceCurrent(target);

        await RunMutationAsync(target, async () =>
        {
            var shot = await UseAsync(target, gameId => Operations.TakeScreenshotAsync(gameId, target.Recording));
            await RefreshStatusAsync(loadPane: false, refreshLatestText: false);
            if (StillShowingRecording() && _state?.Target.SessionId == shot.Event.SessionId)
            {
                _screenshotResult = shot;
                await LoadImageCountsAsync();
            }
        }, "撮影失敗", "ゲーム画像を保存できませんでした。PCのゲーム画面を最前面にして、もう一度撮影してください。");

        if (StillShowingRecording()) FocusAfterRender(FocusTarget.Screenshot);
    }
}
