using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;

namespace GameLauncherWithGit.Components.Pages;

public partial class Capture
{
    private bool _rangeEditorOpen, _finishOpen;
    private PlayRecordingTarget? _finishTarget;
    private long? _resumeWaitingSession;

    private bool HasUnfinishedRange => _rangeSelection is not null || _pendingLiveRange is not null || _snapshot.RangeStart is not null;
    private bool CanSaveBeforeFinish => _rangeSelection is not null || _pendingLiveRange is not null || _snapshot.RangeCanEnd;

    /// <summary>Adapts the current PC capture state for the shared recording controls.</summary>
    private ScenarioRecordingDisplay RecordingDisplay => !ViewingLiveRecord
        ? _snapshot.Profile?.GameId == SelectedGameId && _snapshot.Active ? ScenarioRecordingDisplay.Preparing : ScenarioRecordingDisplay.Unconfigured
        : CaptureStatusLabel != _snapshot.Status ? ScenarioRecordingDisplay.Failed
        : _snapshot.Paused ? ScenarioRecordingDisplay.Paused
        : _snapshot.Recording ? _playState?.CanTakeScreenshot == true ? ScenarioRecordingDisplay.Recording
            : _resumeWaitingSession == _snapshot.SessionId ? ScenarioRecordingDisplay.WaitingAfterResume : ScenarioRecordingDisplay.WaitingForText
        : _snapshot.Connecting || _snapshot.Preview || _snapshot.NeedsProcess ? ScenarioRecordingDisplay.Preparing
        : ScenarioRecordingDisplay.NotRecording;

    /// <summary>Keeps the distinction between a new session and the first body after resuming the same session.</summary>
    private void ObserveResume(CaptureSnapshot previous, CaptureSnapshot current, PlayRecordingState? state)
    {
        if (current.SessionId is null || previous.SessionId != current.SessionId || state?.CanTakeScreenshot == true)
            _resumeWaitingSession = null;
        else if (previous.Paused && current.Recording)
            _resumeWaitingSession = current.SessionId;
    }

    /// <summary>Uses the displayed capture token; normal end only needs a choice when a draft would be lost.</summary>
    private Task RequestFinishAsync(PlayRecordingTarget expected) => RunAsync(async () =>
    {
        RequireFinishTarget(expected);
        _pendingLiveRange = await RecordingOperations.GetDesktopPendingRangeAsync(expected);
        if (!HasUnfinishedRange)
        {
            // Finishing without a draft has no unsaved range to resolve.
            await FinishCoreFromPageAsync(expected);
            return;
        }
        _historyDialog = HistoryDialog.None;
        _finishTarget = expected;
        _finishOpen = true;
        _focusEditor = true;
    }, failureLabel: "記録終了失敗");

    /// <summary>Returns to recording without changing either the local draft or the live range.</summary>
    private void BackFromFinish()
    {
        _finishTarget = null;
        _finishOpen = false;
        _restoreFocus = _restoreToolsFocus = true;
    }

    /// <summary>Opens or freezes the existing range for naming; it does not finish before a successful save.</summary>
    private Task OpenRangeForFinishAsync(PlayRecordingTarget expected) => RunAsync(async () =>
    {
        RequireFinishTarget(expected);
        if (!_finishOpen || _finishTarget != expected) throw ScopeChanged();
        if (_rangeSelection is null)
        {
            // A start marker is resolved by the same service as the normal range-end action.
            var selection = _pendingLiveRange ?? await RecordingOperations.EndDesktopRangeAsync(expected);
            _snapshot = CaptureService.Snapshot;
            _pendingLiveRange = selection;
            OpenRangeDraft(selection, selection.Target);
        }

        _finishTarget = PlayRecordingOperations.DesktopTarget(_snapshot, _profileId);
        _finishOpen = false;
        _rangeEditorOpen = true;
        _focusEditor = true;
    }, failureLabel: "区間固定失敗");

    /// <summary>Completes an explicitly confirmed discard and finish without writing a history range.</summary>
    private Task FinishDiscardingRangeAsync(PlayRecordingTarget expected) => RunAsync(async () =>
    {
        RequireFinishTarget(expected);
        if (!_finishOpen || _finishTarget != expected) throw ScopeChanged();
        await FinishCoreFromPageAsync(expected);
    }, failureLabel: "記録終了失敗");

    /// <summary>Ends the exact session and clears the local draft only if the end succeeded.</summary>
    private async Task FinishCoreFromPageAsync(PlayRecordingTarget expected)
    {
        await RecordingOperations.FinishDesktopAsync(expected);
        ClearRangeDraft();
        _pendingLiveRange = null;
        BackFromFinish();
        await ReadRecordingStateAsync();
        _message = "記録を終了しました。";
    }

    /// <summary>Excludes callbacks captured before a recording session or record changed.</summary>
    private void RequireFinishTarget(PlayRecordingTarget expected)
    {
        if (!ViewingLiveRecord || _playState?.CanFinish != true ||
            PlayRecordingOperations.DesktopTarget(_snapshot, _profileId) != expected)
            throw ScopeChanged();
    }

    /// <summary>Closes just the editor, keeping the captured boundaries and unsaved name and tags.</summary>
    private void HideRangeDraft()
    {
        _rangeEditorOpen = false;
        _restoreFocus = true;
        if (_finishTarget is not null)
        {
            // Back from the editor returns to the finish choice, not to a destructive action.
            _finishOpen = true;
            _focusEditor = true;
            _restoreFocus = false;
        }
    }

    /// <summary>Clears a resolved draft or one whose bound history is no longer displayed.</summary>
    private void ClearRangeDraft()
    {
        _rangeEditorOpen = false;
        _rangeSelection = null;
        _rangeDraftTarget = null;
        _rangeName = "";
        _rangeTags = [];
    }
}
