using System.Runtime.ExceptionServices;
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
    private long _nextStatusRead, _appliedStatusRead;
    private readonly record struct StatusRead(long Order, PlayRecordingState? State);
    private RangeDraft? _rangeDraft;
    private FinishConfirmation? _finishConfirmation;
    private bool _focusNextSharedRange;
    private string RecordingStatus => _state switch
    {
        { Paused: true } => "一時停止中",
        { Recording: true } => "記録中",
        { CanStart: true } => "記録停止中",
        _ => "記録を開始できません"
    };

    private sealed record RangeDraft(PlayRecordingRangeSelection Selection, bool Shared = false, bool AutoFocus = false,
        CaptureTextItem? First = null, CaptureTextItem? Last = null);

    private sealed record FinishConfirmation(string Title, CapturePosition? RangeStart,
        PlayRecordingRangeSelection? PendingRange)
    {
        public bool HasUnsavedRange => RangeStart is not null || PendingRange is not null;

        // A new command version alone (for example, after a screenshot) does not change the impact.
        public bool Matches(PlayRecordingState state) =>
            state.CanFinish &&
            Title == state.Title && Equals(RangeStart, state.RangeStart) && ReferenceEquals(PendingRange, state.PendingRange);
    }

    // A command version can change without replacing the workspace (pause, screenshot, etc.).
    private static bool SameWorkspace(PlayRecordingState? left, PlayRecordingState? right) =>
        left?.GameId == right?.GameId && left?.Target.ProfileId == right?.Target.ProfileId &&
        left?.Target.SessionId == right?.Target.SessionId && left?.Target.ScopeVersion == right?.Target.ScopeVersion;

    private Task<T> WithAccessAsync<T>(long connectionVersion, Func<string?, Task<T>> operation)
    {
        Task<T> WithConnection(string? permittedGameId)
        {
            if (_disposed || !WebSession.IsConnectionCurrent(connectionVersion)) throw new DisplayChangedException();
            return operation(permittedGameId);
        }

        return WebSession.FollowsDesktopTarget
            ? WebSession.UseDesktopTargetAsync(connectionVersion, () => WithConnection(null))
            : WebSession.UseAsync(connectionVersion, permittedGameId => WithConnection(permittedGameId));
    }

    private async Task<StatusRead> GetVisibleStateAsync(long connectionVersion, string? permittedGameId)
    {
        var order = ++_nextStatusRead;
        PlayRecordingState? state;
        try { state = await Operations.GetCurrentStateAsync(); }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            if (!_disposed && !_unauthorized && WebSession.IsConnectionCurrent(connectionVersion) && order > _appliedStatusRead)
            {
                _appliedStatusRead = order;
                if (!_statusError)
                {
                    Logger.LogWarning("Play recording Web status unavailable ({ExceptionType}).", ex.GetType().Name);
                    ClearTargetData();
                    _statusError = true;
                    StateHasChanged();
                }
            }
            throw new DisplayChangedException();
        }
        var visible = state is { BlockedByOtherGame: false } && !string.IsNullOrEmpty(state.GameId)
            && (permittedGameId is null || string.Equals(permittedGameId, state.GameId, StringComparison.Ordinal))
            ? state : null;
        return new(order, visible);
    }

    private async Task<T> UseAsync<T>(DisplayTarget target, Func<string, Task<T>> operation)
    {
        var (value, failure, current) = await WithAccessAsync(target.ConnectionVersion, async permittedGameId =>
        {
            if (!IsDisplayedTargetCurrent(target) ||
                (permittedGameId is not null && !string.Equals(permittedGameId, target.GameId, StringComparison.Ordinal)))
                throw new DisplayChangedException();

            // Operations validate this exact token against the PC under the desktop operation gate.
            T? result = default;
            ExceptionDispatchInfo? failure = null;
            try { result = await operation(target.GameId); }
            catch (Exception ex) when (ex is not UnauthorizedAccessException) { failure = ExceptionDispatchInfo.Capture(ex); }
            if (!IsWorkspaceCurrent(target)) throw new DisplayChangedException();
            return (result, failure, await GetVisibleStateAsync(target.ConnectionVersion, permittedGameId));
        });
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
        catch (Exception ex) when (ex is not (DisplayChangedException or UnauthorizedAccessException))
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
        var current = await WithAccessAsync(connectionVersion, permittedGameId => GetVisibleStateAsync(connectionVersion, permittedGameId));
        ApplyStatus(current, connectionVersion);
        if (statusOnly || _state is not { } state) return;

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
        if (_disposed || _unauthorized || !WebSession.IsConnectionCurrent(connectionVersion)) throw new DisplayChangedException();
        // Status polling can finish while a foreground read is still in flight.
        if (read.Order <= _appliedStatusRead) return;
        _appliedStatusRead = read.Order;
        var state = read.State;
        var changed = !SameWorkspace(_state, state);
        if (changed) ClearTargetData();
        _state = state;
        _statusLoaded = true;
        _statusError = false;
        if (_finishConfirmation is { } confirmation && state is not null && !confirmation.Matches(state)) CloseFinishConfirmation();
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
        _rangeDraft = null;
        _finishConfirmation = null;
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
        _matchingComplete = false;
        _sceneFlow = null;
        _sceneDraft = null;
        _connectionTarget = 0;
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
        _rangeDraft = null;
        await RefreshStatusAsync(loadPane: false);
        if (IsWorkspaceCurrent(target)) FocusAfterRender(FocusTarget.RangeStart);
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
            if (_state is not { } state || !confirmation.Matches(state))
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

    private Task ChangeRecordingAsync(DisplayTarget target, Func<string, Task> operation, string failureLabel, string? rejection = null, FocusTarget? focus = null) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, operation);
        await RefreshStatusAsync(loadPane: true);
        if (focus is { } next && IsWorkspaceCurrent(target)) FocusAfterRender(next);
    }, failureLabel, rejection);

    private Task BreakLivePathAsync(DisplayTarget target) => RunMutationAsync(target, async () =>
    {
        await UseActionAsync(target, gameId => Operations.BreakPathAsync(gameId, target.Recording));
        await RefreshStatusAsync(loadPane: true);
    }, "経路変更失敗");

    private async Task TakeScreenshotAsync(DisplayTarget target)
    {
        bool StillShowingRecording() => !_statusError && IsWorkspaceCurrent(target);

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
