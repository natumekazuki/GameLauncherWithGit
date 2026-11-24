using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;

namespace GameLauncherWithGit.Application.Services;

/// <summary>A server-side rendering token. Keep the displayed value with its callbacks; do not retry stale operations against a new token.</summary>
public sealed record PlayRecordingTarget
{
    internal PlayRecordingTarget(long profileId, long? sessionId, long version)
        => (ProfileId, SessionId, Version) = (profileId, sessionId, version);

    public long ProfileId { get; }
    public long? SessionId { get; }
    internal long Version { get; }
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

    private PlayRecordingTarget CurrentTarget(long profileId)
    {
        var state = capture.OperationState;
        return new(profileId, state.ProfileId == profileId ? state.SessionId : null, state.Version);
    }

    private void RequireTarget(PlayRecordingTarget expected, long profileId, bool allowOtherGame = false)
    {
        var state = capture.OperationState;
        if (new PlayRecordingTarget(profileId, state.ProfileId == profileId ? state.SessionId : null, state.Version) != expected
            || (!allowOtherGame && state.Active && state.ProfileId != profileId)) throw Stale();
    }

    private Task<T> ForGameAsync<T>(string gameId, PlayRecordingTarget expected, Func<Task<T>> action)
        => capture.RunOperationAsync(async () =>
        {
            var profile = await RequireProfileAsync(gameId);
            RequireTarget(expected, profile.Id);
            return await action();
        });

    private Task ForGameAsync(string gameId, PlayRecordingTarget expected, Func<Task> action)
        => ForGameAsync(gameId, expected, async () => { await action(); return true; });

    public Task<PlayRecordingState> GetStateAsync(string gameId) => capture.RunOperationAsync(async () =>
    {
        var profile = await RequireProfileAsync(gameId);
        var live = capture.OperationState;
        var ours = live.ProfileId == profile.Id;
        var recording = ours && live.Formal && !live.Paused;
        var pending = Pending(profile.Id);
        return new PlayRecordingState(CurrentTarget(profile.Id), profile.Title, recording, ours && live.Formal && live.Paused,
            !live.Active && profile.HasConfirmedRecordingSetup, ours && live.Formal,
            ours ? live.RangeStart : null, pending)
        {
            CanStartRange = recording && live.LastBody is not null && live.RangeStart is null && pending is null,
            CanEndRange = recording && live.RangeStart is { } start && live.LastBody?.CompareTo(start) > 0,
            CanTakeScreenshot = recording && live.LastBody is { Sequence: > 0 },
            BlockedByOtherGame = live.Active && !ours
        };
    });

    public Task StartAsync(string gameId, PlayRecordingTarget expected) => capture.RunOperationAsync(async () =>
    {
        var profile = await RequireProfileAsync(gameId);
        RequireTarget(expected, profile.Id);
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
        if (!state.Formal || state.ProfileId != expected.ProfileId || state.SessionId != expected.SessionId) throw Stale();
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
        if (_pendingRange is { } pending && (!state.Formal || pending.CaptureGameId != state.ProfileId
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
        if (Pending(expected.ProfileId) is not null) throw new InvalidOperationException("終了位置を固定した区間を保存または取り消してください。");
        capture.StartRange();
        return Task.CompletedTask;
    }

    private Task<PlayRecordingRangeSelection> EndRangeCore(PlayRecordingTarget expected)
    {
        RequireFormal(expected);
        var bounds = capture.EndRange();
        var selection = new PlayRecordingRangeSelection(CurrentTarget(expected.ProfileId), bounds, live: true);
        _pendingRange = selection;
        return Task.FromResult(selection);
    }

    private Task CancelRangeCore(PlayRecordingTarget expected, PlayRecordingRangeSelection? selection = null)
    {
        RequireFormal(expected);
        var pending = Pending(expected.ProfileId);
        if (selection is not null && !ReferenceEquals(selection, pending)
            || capture.OperationState.RangeStart is null && pending is null) throw Stale();
        _pendingRange = null;
        capture.CancelRange();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CaptureRange>> GetRangesAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, () => store.GetCaptureRangesAsync(expected.ProfileId));

    public Task<PlayRecordingRangeSelection> SelectRangeAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem first, CaptureTextItem last)
        => ForGameAsync(gameId, expected, () => SelectRangeCoreAsync(expected, first, last));

    private async Task<PlayRecordingRangeSelection> SelectRangeCoreAsync(PlayRecordingTarget expected, CaptureTextItem first, CaptureTextItem last)
    {
        first = await RequireItemAsync(expected.ProfileId, first);
        last = await RequireItemAsync(expected.ProfileId, last);
        if (first.StartPosition.CompareTo(last.EndPosition) >= 0) throw new ArgumentException("開始より後の終了位置を選んでください。");
        return new(expected, new(expected.ProfileId, first.StartPosition, last.EndPosition), live: false);
    }

    public Task<long> SaveRangeAsync(string gameId, PlayRecordingTarget expected, PlayRecordingRangeSelection selection,
        string name, IReadOnlyCollection<string> tags) => ForGameAsync(gameId, expected, () => SaveRangeCoreAsync(expected, selection, name, tags));

    private async Task<long> SaveRangeCoreAsync(PlayRecordingTarget expected, PlayRecordingRangeSelection selection, string name, IReadOnlyCollection<string> tags)
    {
        if (selection.Saved || selection.CaptureGameId != expected.ProfileId) throw Stale();
        if (selection.Live)
        {
            RequireFormal(expected);
            if (!ReferenceEquals(selection, Pending(expected.ProfileId))) throw Stale();
        }
        else if (selection.Target != expected) throw Stale();

        await capture.FlushPendingAsync();
        if (selection.Live) RequireFormal(expected);
        var rangeId = await store.SaveCaptureRangeAsync(expected.ProfileId, name, selection.Start, selection.End, tags);
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
        => new(profileId, displayed.Profile?.Id == profileId ? displayed.SessionId : null, displayed.OperationVersion);

    private Task<T> ForDesktopAsync<T>(PlayRecordingTarget expected, bool history, Func<Task<T>> action)
        => capture.RunOperationAsync(() => { RequireTarget(expected, expected.ProfileId, allowOtherGame: history); return action(); });

    private Task ForDesktopAsync(PlayRecordingTarget expected, bool history, Func<Task> action)
        => ForDesktopAsync(expected, history, async () => { await action(); return true; });

    internal Task StartDesktopRangeAsync(PlayRecordingTarget expected) => ForDesktopAsync(expected, false, () => StartRangeCore(expected));
    internal Task<PlayRecordingRangeSelection> EndDesktopRangeAsync(PlayRecordingTarget expected) => ForDesktopAsync(expected, false, () => EndRangeCore(expected));
    internal Task CancelDesktopRangeAsync(PlayRecordingTarget expected) => ForDesktopAsync(expected, false, () => CancelRangeCore(expected));
    internal Task CancelDesktopRangeAsync(PlayRecordingTarget expected, PlayRecordingRangeSelection selection) =>
        ForDesktopAsync(expected, false, () => CancelRangeCore(expected, selection));
    internal Task<PlayRecordingRangeSelection?> GetDesktopPendingRangeAsync(PlayRecordingTarget expected) =>
        ForDesktopAsync(expected, false, () => Task.FromResult(Pending(expected.ProfileId)));
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
