using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Services;

public sealed record PlayRecordingPage(IReadOnlyList<CaptureTextItem> Items, bool HasMore);
public sealed record PlayRecordingObservedFlow(ScenarioFlowGraph Graph, bool MatchingComplete);

public sealed partial class PlayRecordingOperations
{
    // Capture items are returned oldest to newest within each page, with offset counted from the newest end.
    public Task<PlayRecordingPage> GetItemsAsync(string gameId, PlayRecordingTarget expected, int limit = 40, int offset = 0) =>
        ForGameAsync(gameId, expected, async () =>
        {
            if (limit is < 1 or > 100 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit));
            var items = await Store.GetGameCaptureItemsAsync(expected.ProfileId, limit + 1, offset, latest: true);
            return new PlayRecordingPage(items.TakeLast(limit).ToArray(), items.Count > limit);
        });

    public Task<IReadOnlyList<CaptureLabel>> GetLabelsAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, () => Store.GetCaptureLabelsAsync(expected.ProfileId));

    public Task<IReadOnlyList<string>> GetSpeakersAsync(string gameId, PlayRecordingTarget expected) =>
        ForGameAsync(gameId, expected, () => Store.GetGameCaptureSpeakersAsync(expected.ProfileId));

    public Task SetSpeakerAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem item, string? speaker) =>
        ForGameAsync(gameId, expected, () => Store.CorrectCaptureItemAsync(expected.ProfileId, item, changeSpeaker: true, speaker));

    public Task SetLabelAsync(string gameId, PlayRecordingTarget expected, CaptureTextItem item, long? labelId) =>
        ForGameAsync(gameId, expected, () => Store.CorrectCaptureItemAsync(expected.ProfileId, item, changeSpeaker: false, labelId: labelId));

    public Task TakeScreenshotAsync(string gameId, PlayRecordingTarget expected) => ForGameAsync(gameId, expected, () =>
    {
        RequireFormal(expected);
        return Screenshots.CaptureGameAsync(expected.ProfileId, expected.SessionId!.Value);
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
            var current = await RequireItemAsync(expected.ProfileId, item, matchDetails: false);
            await Store.SetScenarioFlowCutAsync(expected.ProfileId, current.StartPosition);
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
            RequireScene(await Scenes.GetScenarioSceneFlowAsync(expected.ProfileId), scene);
            await Scenes.UpdateScenarioSceneAsync(expected.ProfileId, scene.Id, edit);
            Capture.ChangeOperationVersion();
        });

    public Task SetSceneConnectionAsync(string gameId, PlayRecordingTarget expected, ScenarioScene from, ScenarioScene to, bool connected) =>
        ForGameAsync(gameId, expected, async () =>
        {
            var snapshot = await Scenes.GetScenarioSceneFlowAsync(expected.ProfileId);
            RequireScene(snapshot, from);
            RequireScene(snapshot, to);
            await Scenes.SetScenarioSceneConnectionAsync(expected.ProfileId, from.Id, to.Id, connected);
            Capture.ChangeOperationVersion();
        });

    private static void RequireScene(ScenarioSceneFlowSnapshot flow, ScenarioScene scene)
    {
        var current = flow.Scenes.SingleOrDefault(x => x.Id == scene.Id)?.Definition;
        var expected = scene.Definition;
        if (current is null || current.Title != expected.Title || current.DisplayNumber != expected.DisplayNumber
            || current.Chapter != expected.Chapter || current.X != expected.X || current.Y != expected.Y
            || !current.RangeIds.SequenceEqual(expected.RangeIds) || !current.TagIds.SequenceEqual(expected.TagIds)) throw Stale();
    }
}
