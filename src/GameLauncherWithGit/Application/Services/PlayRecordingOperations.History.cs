using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Services;

public sealed record PlayRecordingPage(IReadOnlyList<CaptureTextItem> Items, bool HasMore)
{
    public CapturePosition? NextBefore => HasMore && Items.Count > 0 ? Items[0].StartPosition : null;
}
public sealed record PlayRecordingObservedFlow(ScenarioFlowGraph Graph, bool MatchingComplete);

public sealed partial class PlayRecordingOperations
{
    /// <summary>
    /// Returns oldest to newest within the page. Supply the previous page's NextBefore when loading older text
    /// during live capture so newly appended text does not move the page boundary.
    /// </summary>
    public Task<PlayRecordingPage> GetItemsAsync(string gameId, PlayRecordingTarget expected, int limit = 40, int offset = 0,
        CapturePosition? before = null, long? rangeId = null) =>
        ForGameAsync(gameId, expected, async () =>
        {
            if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
            if (offset < 0 || (before is not null && offset != 0)) throw new ArgumentOutOfRangeException(nameof(offset));
            var items = await Store.GetGameCaptureItemsAsync(expected.ProfileId, limit + 1, offset, latest: true, rangeId: rangeId, before: before);
            return new PlayRecordingPage(items.TakeLast(limit).ToArray(), items.Count > limit);
        });

    public Task<CaptureTextItem> GetItemAtAsync(string gameId, PlayRecordingTarget expected, CapturePosition position) =>
        ForGameAsync(gameId, expected, () => RequireItemAtAsync(expected.ProfileId, position));

    private async Task<CaptureTextItem> RequireItemAtAsync(long profileId, CapturePosition position) =>
        await Store.FindCaptureItemAtAsync(profileId, position)
            ?? throw new InvalidOperationException("選択した位置の本文が見つかりません。記録を読み込み直してください。");

    public Task<IReadOnlyList<CaptureLabel>> GetLabelsAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, () => Store.GetCaptureLabelsAsync(expected.ProfileId));

    public Task<IReadOnlyList<string>> GetSpeakersAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, () => Store.GetGameCaptureSpeakersAsync(expected.ProfileId));

    public Task SetSpeakerAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem item, string? speaker) =>
        ForGameAsync(gameId, expected, () => Store.CorrectCaptureItemAsync(expected.ProfileId, item, changeSpeaker: true, speaker));

    public Task SetLabelAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem item, long? labelId) =>
        ForGameAsync(gameId, expected, () => Store.CorrectCaptureItemAsync(expected.ProfileId, item, changeSpeaker: false, labelId: labelId));

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
