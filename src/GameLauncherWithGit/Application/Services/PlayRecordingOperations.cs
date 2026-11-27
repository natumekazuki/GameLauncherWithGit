using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;

namespace GameLauncherWithGit.Application.Services;

/// <summary>A server-side rendering token. Keep the displayed value with its callbacks; do not retry stale operations against a new token.</summary>
public sealed record PlayRecordingTarget
{
    internal PlayRecordingTarget(long profileId, long? sessionId, long version, string? gameId = null, long? recordId = null,
        long scopeVersion = 0, bool fixedRecord = false, long? ownerProfileId = null)
        => (ProfileId, SessionId, Version, GameId, RecordId, ScopeVersion, FixedRecord, OwnerProfileId) =
            (profileId, sessionId, version, gameId, recordId, scopeVersion, fixedRecord, ownerProfileId);

    public long ProfileId { get; }
    public long? SessionId { get; }
    public long? RecordId { get; }
    internal string? GameId { get; }
    internal long Version { get; }
    internal long ScopeVersion { get; }
    internal bool FixedRecord { get; }
    internal long? OwnerProfileId { get; }
    internal ScenarioRecordTargetResolver.DesktopHistoryObservation? HistoryObservation { get; init; }
    internal long HistoryRoot => RecordId ?? ProfileId;
}

/// <summary>Frozen boundaries. The same selection can only be saved once; name and tags are not shared drafts.</summary>
public sealed class PlayRecordingRangeSelection
{
    internal PlayRecordingRangeSelection(PlayRecordingTarget target, CaptureRangeSelection bounds, bool live)
        => (Target, Bounds, Live) = (target, bounds, live);

    public long CaptureGameId => Bounds.CaptureGameId;
    public CapturePosition Start => Bounds.Start;
    public CapturePosition End => Bounds.End;
    internal CaptureRangeSelection Bounds { get; }
    internal PlayRecordingTarget Target { get; }
    internal bool Live { get; }
    internal bool Saved { get; set; }
}

public sealed record PlayRecordingState(PlayRecordingTarget Target, string Title, bool Recording, bool Paused,
    bool CanStart, bool CanFinish, CapturePosition? RangeStart, PlayRecordingRangeSelection? PendingRange)
{
    public string GameId { get; init; } = "";
    /// <summary>The current Web recording root's branch, if it is branch-specific.</summary>
    public string? RecordingBranchRef { get; init; }
    public bool CanStartRange { get; init; }
    public bool CanEndRange { get; init; }
    public bool CanTakeScreenshot { get; init; }
    public bool BlockedByOtherGame { get; init; }
}

/// <summary>
/// Operations on the existing capture and stores. The host must authorize and serialize the supplied game ID
/// with grant changes. This boundary revalidates the selected profile and recording under the desktop capture gate.
/// </summary>
public sealed partial class PlayRecordingOperations(ScenarioCaptureService capture, IScenarioCaptureStore store,
    ScenarioScreenshotService screenshots, IScenarioSceneFlowStore scenes, IScenarioMaterialReader? material = null)
{
    private PlayRecordingRangeSelection? _pendingRange;
    private ScenarioCaptureService Capture => capture;
    private IScenarioCaptureStore Store => store;
    private ScenarioScreenshotService Screenshots => screenshots;
    private IScenarioSceneFlowStore Scenes => scenes;
    private IScenarioMaterialReader? Material => material ?? store as IScenarioMaterialReader;

    private static InvalidOperationException Stale() => new("記録対象または表示が変わりました。更新してから操作してください。");

    private async Task<CaptureProfile> RequireProfileAsync(string gameId)
        => await store.FindCaptureProfileAsync(gameId) ?? throw new InvalidOperationException("設定済みのゲームが見つかりません。");

    private PlayRecordingTarget CurrentTarget(long recordId, string? gameId = null)
    {
        var state = capture.OperationState;
        var play = capture.CurrentPlayTarget;
        if (gameId is null && play.RecordId == recordId)
            gameId = play.Profile?.GameId;
        return new(recordId, state.RecordId == recordId ? state.SessionId : null, state.Version, gameId, recordId, play.ScopeVersion);
    }

    private void RequireGameTarget(string gameId, PlayRecordingTarget expected, long profileId)
    {
        var state = capture.OperationState;
        if (!capture.UsesPcPlayTarget)
        {
            if (CurrentTarget(profileId) != expected || (state.Active && state.ProfileId != profileId)) throw Stale();
            return;
        }
        var selected = capture.CurrentPlayTarget;
        if (selected.Profile?.GameId != gameId || selected.Profile.Id != profileId || selected.RecordId is not { } root
            || CurrentTarget(root, gameId) != expected) throw Stale();
    }

    private Task<T> ForGameAsync<T>(string gameId, PlayRecordingTarget expected, Func<Task<T>> action)
        => capture.RunOperationAsync(async () =>
        {
            await capture.RefreshPlayTargetCoreAsync();
            var profile = await RequireProfileAsync(gameId);
            RequireGameTarget(gameId, expected, profile.Id);
            return await action();
        });

    private Task ForGameAsync(string gameId, PlayRecordingTarget expected, Func<Task> action)
        => ForGameAsync(gameId, expected, async () => { await action(); return true; });

    /// <summary>Returns only the PC's current, resolved play target; null means wait for a valid selection.</summary>
    public Task<PlayRecordingState?> GetCurrentStateAsync() => capture.RunOperationAsync<PlayRecordingState?>(async () =>
    {
        await capture.RefreshPlayTargetCoreAsync();
        var selected = capture.CurrentPlayTarget;
        if (selected.Profile?.GameId is not { } gameId || selected.RecordId is not { } recordId) return null;
        var profile = await store.FindCaptureProfileAsync(gameId);
        if (profile is null || capture.CurrentPlayTarget.RecordId != recordId) return null;
        // Use metadata for the same recording root; never resolve or open another branch.
        string? branch = null;
        if (recordId != profile.Id && store is IScenarioHistoryStore history)
        {
            var scopes = await history.GetScenarioHistoryScopesAsync(profile.Id);
            branch = scopes.FirstOrDefault(scope => scope.RecordId == recordId)?.BranchRef;
        }
        var after = capture.CurrentPlayTarget;
        if (after.RecordId != recordId || after.ScopeVersion != selected.ScopeVersion)
            return null;
        return BuildState(profile, recordId) with { RecordingBranchRef = branch };
    });

    /// <summary>Reads state for a known game. In the app, a non-current or unresolved game cannot be operated.</summary>
    public Task<PlayRecordingState> GetStateAsync(string gameId) => capture.RunOperationAsync(async () =>
    {
        await capture.RefreshPlayTargetCoreAsync();
        var profile = await RequireProfileAsync(gameId);
        if (!capture.UsesPcPlayTarget) return BuildState(profile, profile.Id);
        var selected = capture.CurrentPlayTarget;
        if (selected.Profile?.GameId == gameId && selected.RecordId is { } recordId) return BuildState(profile, recordId);
        return new PlayRecordingState(new(0, null, capture.OperationState.Version, gameId, scopeVersion: selected.ScopeVersion), profile.Title,
            false, false, false, false, null, null) { GameId = gameId, BlockedByOtherGame = true };
    });

    private PlayRecordingState BuildState(CaptureProfile profile, long recordId)
    {
        var live = capture.OperationState;
        var ours = live.RecordId == recordId;
        var recording = ours && live.Formal && !live.Paused;
        var pending = Pending(recordId);
        var gameId = profile.GameId ?? "";
        return new PlayRecordingState(CurrentTarget(recordId, capture.UsesPcPlayTarget ? gameId : null), profile.Title, recording, ours && live.Formal && live.Paused,
            capture.CanStartConfigured(profile), ours && live.Formal,
            ours ? live.RangeStart : null, pending)
        {
            GameId = gameId,
            CanStartRange = ours && live.RangeCanStart && pending is null,
            CanEndRange = recording && live.RangeStart is { } start && live.LastBody?.CompareTo(start) > 0,
            CanTakeScreenshot = recording && live.LastBody is { Sequence: > 0 },
            BlockedByOtherGame = live.Active && !ours
        };
    }

    public Task StartAsync(string gameId, PlayRecordingTarget expected) => capture.RunOperationAsync(async () =>
    {
        await capture.RefreshPlayTargetCoreAsync();
        var profile = await RequireProfileAsync(gameId);
        RequireGameTarget(gameId, expected, profile.Id);
        try { await capture.StartConfiguredCoreAsync(profile); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("設定済みのゲームに接続できませんでした。PCで対象ゲームと取得設定を確認してください。");
        }
    });

    public Task PauseAsync(string gameId, PlayRecordingTarget expected) => ForGameAsync(gameId, expected, () => PauseCore(expected, true));
    public Task ResumeAsync(string gameId, PlayRecordingTarget expected) => ForGameAsync(gameId, expected, () => PauseCore(expected, false));
    public Task FinishAsync(string gameId, PlayRecordingTarget expected) => ForGameAsync(gameId, expected, () =>
    {
        RequireFormal(expected);
        return capture.FinishCoreAsync(reportFailure: true);
    });

    private void RequireFormal(PlayRecordingTarget expected)
    {
        var state = capture.OperationState;
        if (!state.Formal || state.RecordId != expected.HistoryRoot || state.SessionId != expected.SessionId) throw Stale();
    }

    private Task PauseCore(PlayRecordingTarget expected, bool pause)
    {
        RequireFormal(expected);
        if (pause) capture.PauseRecording(); else capture.ResumeRecording();
        return Task.CompletedTask;
    }

    private PlayRecordingRangeSelection? Pending(long profileId)
    {
        var state = capture.OperationState;
        if (_pendingRange is { } pending && (!state.Formal || pending.CaptureGameId != state.RecordId
            || pending.Target.SessionId != state.SessionId)) _pendingRange = null;
        return _pendingRange?.CaptureGameId == profileId ? _pendingRange : null;
    }

    public Task StartRangeAsync(string gameId, PlayRecordingTarget expected) => ForGameAsync(gameId, expected, () => StartRangeCore(expected));
    public Task<PlayRecordingRangeSelection> EndRangeAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, () => EndRangeCore(expected));
    public Task CancelRangeAsync(string gameId, PlayRecordingTarget expected) => ForGameAsync(gameId, expected, () => CancelRangeCore(expected));

    private Task StartRangeCore(PlayRecordingTarget expected)
    {
        RequireFormal(expected);
        if (Pending(expected.HistoryRoot) is not null) throw new InvalidOperationException("終了位置を固定した区間を保存または取り消してください。");
        capture.StartRange();
        return Task.CompletedTask;
    }

    private Task<PlayRecordingRangeSelection> EndRangeCore(PlayRecordingTarget expected)
    {
        RequireFormal(expected);
        var bounds = capture.EndRange();
        var selection = new PlayRecordingRangeSelection(CurrentTarget(expected.HistoryRoot, expected.GameId), bounds, live: true);
        _pendingRange = selection;
        return Task.FromResult(selection);
    }

    private Task CancelRangeCore(PlayRecordingTarget expected, PlayRecordingRangeSelection? selection = null)
    {
        RequireFormal(expected);
        var pending = Pending(expected.HistoryRoot);
        if (selection is not null && !ReferenceEquals(selection, pending)
            || capture.OperationState.RangeStart is null && pending is null) throw Stale();
        _pendingRange = null;
        capture.CancelRange();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CaptureRange>> GetRangesAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, () => store.GetCaptureRangesAsync(expected.HistoryRoot));

    public Task<PlayRecordingRangeSelection> SelectRangeAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem first, CaptureTextItem last)
        => ForGameAsync(gameId, expected, () => SelectRangeCoreAsync(expected, first, last));

    private async Task<PlayRecordingRangeSelection> SelectRangeCoreAsync(PlayRecordingTarget expected, CaptureTextItem first, CaptureTextItem last)
    {
        first = await RequireItemAsync(expected.HistoryRoot, first);
        last = await RequireItemAsync(expected.HistoryRoot, last);
        if (first.StartPosition.CompareTo(last.EndPosition) >= 0) throw new ArgumentException("開始より後の終了位置を選んでください。");
        return new(expected, new(expected.HistoryRoot, first.StartPosition, last.EndPosition), live: false);
    }

    public Task<long> SaveRangeAsync(string gameId, PlayRecordingTarget expected, PlayRecordingRangeSelection selection,
        string name, IReadOnlyCollection<string> tags) => ForGameAsync(gameId, expected, () => SaveRangeCoreAsync(expected, selection, name, tags));

    private async Task<long> SaveRangeCoreAsync(PlayRecordingTarget expected, PlayRecordingRangeSelection selection, string name, IReadOnlyCollection<string> tags)
    {
        if (selection.Saved || selection.CaptureGameId != expected.HistoryRoot) throw Stale();
        if (selection.Live)
        {
            RequireFormal(expected);
            if (!ReferenceEquals(selection, Pending(expected.HistoryRoot))) throw Stale();
        }
        else if (selection.Target != expected) throw Stale();

        await capture.FlushPendingAsync();
        if (selection.Live) RequireFormal(expected);
        var rangeId = await store.SaveCaptureRangeAsync(expected.HistoryRoot, name, selection.Start, selection.End, tags);
        selection.Saved = true;
        if (selection.Live) _pendingRange = null;
        capture.ChangeOperationVersion();
        return rangeId;
    }

    private async Task<CaptureTextItem> RequireItemAsync(long profileId, CaptureTextItem expected)
    {
        var item = await store.FindCaptureItemAsync(profileId, expected.Id);
        if (item is null || item.SessionId != expected.SessionId || item.Sequence != expected.Sequence
            || item.Start != expected.Start || item.Length != expected.Length) throw Stale();
        return item;
    }

    internal static PlayRecordingTarget DesktopTarget(CaptureSnapshot displayed, long profileId)
    {
        var matches = displayed.Profile?.Id == profileId || displayed.RecordId == profileId;
        return new(profileId, matches ? displayed.SessionId : null, displayed.OperationVersion,
            matches ? displayed.Profile?.GameId : null, matches ? displayed.RecordId : profileId, displayed.PlayScopeVersion);
    }

    // Explicitly identify the owner and root shown by a history UI (including when not extracting that game).
    internal static PlayRecordingTarget DesktopHistoryTarget(CaptureSnapshot displayed, string gameId, long recordId,
        bool fixedRecord = false, ScenarioRecordTargetResolver.DesktopHistoryObservation? observation = null)
        => new(recordId, displayed.PlayGameId == gameId && displayed.RecordId == recordId ? displayed.SessionId : null,
            displayed.OperationVersion, gameId, recordId, displayed.PlayScopeVersion, fixedRecord) { HistoryObservation = observation };

    // Fixed PC history can also belong to a retained profile whose game registration was removed.
    internal static PlayRecordingTarget DesktopHistoryTarget(CaptureSnapshot displayed, long profileId, long recordId)
        => new(recordId, displayed.Profile?.Id == profileId && displayed.RecordId == recordId ? displayed.SessionId : null,
            displayed.OperationVersion, displayed.Profile?.Id == profileId ? displayed.Profile.GameId : null,
            recordId, displayed.PlayScopeVersion, fixedRecord: true, ownerProfileId: profileId);

    private async Task RequireDesktopTargetAsync(PlayRecordingTarget expected, bool history)
    {
        var shown = capture.Snapshot;
        if (history && capture.UsesPcPlayTarget)
        {
            if (expected.RecordId is not { } recordId) throw Stale();
            if (expected.FixedRecord && expected.OwnerProfileId is { } owner)
            {
                if (!await capture.OwnsDesktopRecordAsync(owner, recordId)
                    || DesktopHistoryTarget(capture.Snapshot, owner, recordId) != expected) throw Stale();
                return;
            }
            if (expected.GameId is not { } gameId) throw Stale();
            if (expected.FixedRecord)
            {
                if (!await capture.OwnsDesktopRecordAsync(gameId, recordId)) throw Stale();
            }
            else
            {
                var play = capture.CurrentPlayTarget;
                if (play.Profile?.GameId == gameId)
                {
                    if (expected.HistoryObservation is not null || play.RecordId != recordId) throw Stale();
                }
                else if (expected.HistoryObservation is not { } observation
                    || !await observation.IsCurrentAsync(gameId, recordId)) throw Stale();
            }
            if (DesktopHistoryTarget(capture.Snapshot, gameId, recordId, expected.FixedRecord, expected.HistoryObservation) != expected)
                throw Stale();
            return;
        }
        if (DesktopTarget(shown, expected.ProfileId) != expected
            || (!history && shown.Active && shown.Profile?.Id != expected.ProfileId && shown.RecordId != expected.ProfileId)
            || (!history && capture.UsesPcPlayTarget && (expected.GameId is null || expected.GameId != shown.Profile?.GameId))) throw Stale();
    }

    private Task<T> ForDesktopAsync<T>(PlayRecordingTarget expected, bool history, Func<Task<T>> action)
        => capture.RunOperationAsync(async () =>
        {
            await capture.RefreshPlayTargetCoreAsync();
            await RequireDesktopTargetAsync(expected, history);
            return await action();
        });

    private Task ForDesktopAsync(PlayRecordingTarget expected, bool history, Func<Task> action)
        => ForDesktopAsync(expected, history, async () => { await action(); return true; });

    // PC-only history editors use a frozen target. Retrying a rejected operation requires a fresh user action.
    internal Task<T> RunDesktopHistoryAsync<T>(PlayRecordingTarget expected, Func<long, Task<T>> action)
        => ForDesktopAsync(expected, true, () => action(expected.HistoryRoot));

    internal Task RunDesktopHistoryAsync(PlayRecordingTarget expected, Func<long, Task> action)
        => ForDesktopAsync(expected, true, () => action(expected.HistoryRoot));

    internal Task StartDesktopRangeAsync(PlayRecordingTarget expected) => ForDesktopAsync(expected, false, () => StartRangeCore(expected));
    internal Task<PlayRecordingRangeSelection> EndDesktopRangeAsync(PlayRecordingTarget expected) => ForDesktopAsync(expected, false, () => EndRangeCore(expected));
    internal Task CancelDesktopRangeAsync(PlayRecordingTarget expected) => ForDesktopAsync(expected, false, () => CancelRangeCore(expected));
    internal Task CancelDesktopRangeAsync(PlayRecordingTarget expected, PlayRecordingRangeSelection selection) =>
        ForDesktopAsync(expected, false, () => CancelRangeCore(expected, selection));
    internal Task<PlayRecordingRangeSelection?> GetDesktopPendingRangeAsync(PlayRecordingTarget expected) =>
        ForDesktopAsync(expected, false, () => Task.FromResult(Pending(expected.HistoryRoot)));
    internal Task<PlayRecordingRangeSelection> SelectDesktopRangeAsync(PlayRecordingTarget expected, CaptureTextItem first, CaptureTextItem last)
        => ForDesktopAsync(expected, true, () => SelectRangeCoreAsync(expected, first, last));
    internal Task<long> SaveDesktopRangeAsync(PlayRecordingTarget expected, PlayRecordingRangeSelection selection, string name, IReadOnlyCollection<string> tags)
        => ForDesktopAsync(expected, !selection.Live, () => SaveRangeCoreAsync(expected, selection, name, tags));
    internal Task PauseDesktopAsync(PlayRecordingTarget expected, bool pause) => ForDesktopAsync(expected, false, () => PauseCore(expected, pause));
    internal Task FinishDesktopAsync(PlayRecordingTarget expected) => ForDesktopAsync(expected, false, () => capture.FinishCoreAsync(reportFailure: true));
    internal Task ConnectDesktopAsync(PlayRecordingTarget expected, int processId) => ForDesktopAsync(expected, false, () => capture.SelectProcessCoreAsync(processId));
    internal Task ConfirmDesktopAsync(PlayRecordingTarget expected) => ForDesktopAsync(expected, false, () => capture.ConfirmProcessCoreAsync());
    internal Task SaveDesktopRulesAsync(PlayRecordingTarget expected, CaptureRules rules, CaptureSource? source)
        => ForDesktopAsync(expected, false, () => capture.SaveRulesCoreAsync(rules, source));
}
