using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;

namespace GameLauncherWithGit.Application.Services;

public sealed record PlayRecordingPage(IReadOnlyList<CaptureTextItem> Items, bool HasMore)
{
    public CapturePosition? NextBefore => HasMore && Items.Count > 0 ? Items[0].StartPosition : null;
}
public sealed record PlayRecordingObservedFlow(ScenarioFlowGraph Graph, bool MatchingComplete);

public sealed partial class PlayRecordingOperations
{
    /// <summary>Reads the committed invalidation signal; actual history still requires the Web target guard.</summary>
    internal ScenarioLiveFeedVersion? LiveFeedVersion => Capture.LiveFeedVersion;
    internal ScenarioLiveFeedVersion? LiveFeedSpeakerVersion => Capture.LiveFeedSpeakerVersion;

    internal event Action<ScenarioLiveFeedVersion> LiveFeedCommitted
    {
        add => Capture.LiveFeedCommitted += value;
        remove => Capture.LiveFeedCommitted -= value;
    }

    internal long ScreenshotSavedCount => Screenshots.Snapshot.SavedCount;

    /// <summary>Loads a bounded live page only for the authorized target matching the captured version.</summary>
    internal Task<PlayRecordingPage> GetLiveItemsAsync(string gameId, PlayRecordingTarget expected,
        ScenarioLiveFeedVersion version, int limit)
    {
        if (!version.Matches(expected.HistoryRoot, expected.SessionId, expected.ScopeVersion)) throw Stale();
        return GetItemsAsync(gameId, expected, limit);
    }

    /// <summary>
    /// Returns oldest to newest within the page. Supply the previous page's NextBefore when loading older text
    /// during live capture so newly appended text does not move the page boundary.
    /// </summary>
    public Task<PlayRecordingPage> GetItemsAsync(string gameId, PlayRecordingTarget expected, int limit = 40, int offset = 0,
        CapturePosition? before = null, long? rangeId = null, string? textSearch = null, string? speakerSearch = null) =>
        ReadTextPageAsync(gameId, expected, limit, offset, before, rangeId, textSearch, speakerSearch, null);

    /// <summary>Opens a transient observed interval through the same authorized history read.</summary>
    internal Task<PlayRecordingPage> GetObservedIntervalItemsAsync(string gameId, PlayRecordingTarget expected,
        CaptureRangeSelection interval, int limit, CapturePosition? before, string? textSearch, string? speakerSearch) =>
        ReadTextPageAsync(gameId, expected, limit, 0, before, null, textSearch, speakerSearch, interval);

    private Task<PlayRecordingPage> ReadTextPageAsync(string gameId, PlayRecordingTarget expected, int limit, int offset,
        CapturePosition? before, long? rangeId, string? textSearch, string? speakerSearch, CaptureRangeSelection? interval) =>
        ForGameAsync(gameId, expected, async () =>
        {
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            if (offset < 0 || (before is not null && offset != 0)) throw new ArgumentOutOfRangeException(nameof(offset));
            var items = await Store.GetGameCaptureItemsAsync(expected.ProfileId, limit + 1, offset, latest: true,
                rangeId: rangeId, textSearch: textSearch, speakerSearch: speakerSearch, before: before, interval: interval);
            return new PlayRecordingPage(items.TakeLast(limit).ToArray(), items.Count > limit);
        });

    /// <summary>Returns only image counts for the displayed page, under the same recording target guard as its text.</summary>
    public Task<IReadOnlyDictionary<CaptureEventKey, int>> GetScreenshotCountsAsync(string gameId,
        PlayRecordingTarget expected, IReadOnlyCollection<CaptureEventKey> events) =>
        ForGameAsync(gameId, expected, () => Store is IScenarioScreenshotStore screenshots
            ? screenshots.GetScreenshotCountsAsync(expected.ProfileId, events)
            : throw new InvalidOperationException("画像の枚数を取得できません。"));

    public Task<CaptureTextItem> GetItemAtAsync(string gameId, PlayRecordingTarget expected, CapturePosition position) =>
        ForGameAsync(gameId, expected, () => RequireItemAtAsync(expected.ProfileId, position));

    /// <summary>Updates only the saved range intersecting the selected occurrence in this Web recording.</summary>
    internal Task RenameDisplayedRangeAsync(string gameId, PlayRecordingTarget expected, CapturePosition occurrence,
        long rangeId, string name, IReadOnlyCollection<string> tags) =>
        ForGameAsync(gameId, expected, async () =>
        {
            var recordId = expected.HistoryRoot;
            var item = await RequireItemAtAsync(recordId, occurrence);
            var range = (await Store.GetCaptureRangesAsync(recordId)).FirstOrDefault(x => x.Id == rangeId);
            // The ID must still belong to this record and intersect the text displayed when editing began.
            if (range is null || item.EndPosition.CompareTo(range.Start) <= 0 || item.StartPosition.CompareTo(range.End) >= 0)
                throw new InvalidOperationException("選んだ本文の区間が見つかりません。");
            await Store.UpdateCaptureRangeAsync(recordId, rangeId, name, tags);
            Capture.ChangeOperationVersion();
        });

    private async Task<CaptureTextItem> RequireItemAtAsync(long profileId, CapturePosition position) =>
        await Store.FindCaptureItemAtAsync(profileId, position)
            ?? throw new InvalidOperationException("選択した位置の本文が見つかりません。記録を読み込み直してください。");

    public Task<IReadOnlyList<CaptureLabel>> GetLabelsAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, async () => await Store.GetCaptureLabelsAsync((await RequireProfileAsync(gameId)).Id));

    public Task<IReadOnlyList<string>> GetSpeakersAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, () => Store.GetGameCaptureSpeakersAsync(expected.ProfileId));

    public Task SetSpeakerAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem item, string? speaker) =>
        CorrectTextAsync(gameId, expected, item, changeLabel: false, labelId: null, changeSpeaker: true, speaker);

    public Task SetLabelAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem item, long? labelId) =>
        CorrectTextAsync(gameId, expected, item, changeLabel: true, labelId, changeSpeaker: false, speaker: null);

    public Task CorrectTextAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem item,
        bool changeLabel, long? labelId, bool changeSpeaker, string? speaker) =>
        ForGameAsync(gameId, expected, () => Store.CorrectCaptureItemAsync(expected.ProfileId, item, changeLabel, labelId, changeSpeaker, speaker));

    public Task<PlayRecordingScreenshotResult> TakeScreenshotAsync(string gameId, PlayRecordingTarget expected) => ForGameAsync(gameId, expected, async () =>
    {
        RequireFormal(expected);
        var result = await Screenshots.CaptureGameAsync(expected.ProfileId, expected.SessionId!.Value);
        Capture.ChangeOperationVersion();
        return result;
    });

    public Task<IReadOnlyList<PlayRecordingScreenshot>> GetScreenshotsAsync(string gameId, PlayRecordingTarget expected, CaptureEventKey key) =>
        ForGameAsync(gameId, expected, () => Screenshots.GetPlayScreenshotsAsync(expected.ProfileId, key));

    public Task<byte[]> ReadScreenshotAsync(string gameId, PlayRecordingTarget expected, CaptureEventKey key, string imageId) =>
        ForGameAsync(gameId, expected, () => Screenshots.ReadPlayScreenshotAsync(expected.ProfileId, key, imageId));

    public Task BreakPathAsync(string gameId, PlayRecordingTarget expected) => ForGameAsync(gameId, expected, () => BreakPathCore(expected));

    internal Task BreakDesktopPathAsync(PlayRecordingTarget expected) => ForDesktopAsync(expected, false, () => BreakPathCore(expected));

    private Task BreakPathCore(PlayRecordingTarget expected)
    {
        RequireFormal(expected);
        Capture.BreakPath();
        return Task.CompletedTask;
    }

    public Task CutPathAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem item) =>
        ForGameAsync(gameId, expected, async () =>
        {
            var current = await RequireItemAsync(expected.ProfileId, item);
            await Store.SetScenarioFlowCutAsync(expected.ProfileId, current.StartPosition);
            Capture.ChangeOperationVersion();
        });

    public Task CutPathAsync(string gameId, PlayRecordingTarget expected, CapturePosition position) =>
        ForGameAsync(gameId, expected, async () =>
        {
            await RequireItemAtAsync(expected.ProfileId, position);
            await Store.SetScenarioFlowCutAsync(expected.ProfileId, position);
            Capture.ChangeOperationVersion();
        });

    public Task<PlayRecordingObservedFlow> GetObservedFlowAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, async () =>
        {
            var reader = Material ?? throw new InvalidOperationException("記録経路を読み込めません。");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var snapshot = await reader.GetScenarioMaterialAsync(expected.ProfileId, new());
                var overrides = await Store.GetScenarioFlowOverridesAsync(expected.ProfileId);
                if (snapshot.Version == await reader.GetScenarioMaterialVersionAsync(expected.ProfileId))
                    return new PlayRecordingObservedFlow(ScenarioFlowMaterialAdapter.Build(snapshot, overrides), snapshot.MatchingComplete);
            }
            throw new InvalidOperationException("記録が更新されました。経路を読み込み直してください。");
        });

    /// <summary>Projects the scope-checked snapshot for the recording UI only; the material contract is unchanged.</summary>
    internal Task<(ScenarioRangeFlowGraph Graph, bool MatchingComplete)> GetRangeFlowAsync(
        string gameId, PlayRecordingTarget expected) => ForGameAsync(gameId, expected, async () =>
    {
        var reader = Material ?? throw new InvalidOperationException("記録経路を読み込めません。");
        var snapshot = await reader.GetScenarioMaterialAsync(expected.HistoryRoot, new());
        return (await Task.Run(() => ScenarioRangeFlowProjector.Build(snapshot)), snapshot.MatchingComplete);
    });

    public Task<ScenarioSceneFlowSnapshot> GetSceneFlowAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, () => Scenes.GetScenarioSceneFlowAsync(expected.ProfileId));

    public Task<long> CreateSceneAsync(string gameId, PlayRecordingTarget expected, ScenarioSceneEdit edit) =>
        ForGameAsync(gameId, expected, async () =>
        {
            var id = await Scenes.CreateScenarioSceneAsync(expected.ProfileId, edit);
            Capture.ChangeOperationVersion();
            return id;
        });

    public Task UpdateSceneAsync(string gameId, PlayRecordingTarget expected, ScenarioScene scene, ScenarioSceneEdit edit) =>
        ForGameAsync(gameId, expected, async () =>
        {
            await Scenes.UpdateDisplayedScenarioSceneAsync(expected.ProfileId, scene, edit);
            Capture.ChangeOperationVersion();
        });

    public Task SetSceneConnectionAsync(string gameId, PlayRecordingTarget expected, ScenarioScene from, ScenarioScene to, bool connected) =>
        ForGameAsync(gameId, expected, async () =>
        {
            await Scenes.SetDisplayedScenarioSceneConnectionAsync(expected.ProfileId, from, to, connected);
            Capture.ChangeOperationVersion();
        });

}
