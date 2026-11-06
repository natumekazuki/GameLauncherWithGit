using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioCaptureStore
{
    Task<CaptureProfile> GetOrCreateCaptureProfileAsync(string gameId);
    Task<IReadOnlyList<CaptureProfile>> GetCaptureProfilesAsync();
    Task SaveCaptureProfileAsync(CaptureProfile profile);
    Task<IReadOnlyList<CaptureLabel>> GetCaptureLabelsAsync(long profileId);
    Task<long> BeginCaptureSessionAsync(long profileId);
    Task EndCaptureSessionAsync(long sessionId);
    Task<IReadOnlyList<CaptureSession>> GetCaptureSessionsAsync(long profileId);
    Task<IReadOnlyList<CaptureTextItem>> GetGameCaptureItemsAsync(long profileId, int limit = 200, int offset = 0, long? labelId = null, bool unclassifiedOnly = false, bool latest = false, long? rangeId = null, CaptureKind? kind = null, string? textSearch = null, string? speakerSearch = null);
    Task<IReadOnlyList<CaptureDeletionTarget>> GetCaptureDeletionTargetsAsync(long profileId, IReadOnlyCollection<long> itemIds);
    Task DeleteCaptureEventsAsync(long profileId, IReadOnlyCollection<CaptureEventKey> events);
    Task<CaptureHistoryCounts> GetCaptureHistoryCountsAsync(long profileId);
    Task ClearCaptureHistoryAsync(long profileId);
    Task RenameCaptureRangeAsync(long profileId, long rangeId, string name);
    Task DeleteCaptureRangeAsync(long profileId, long rangeId);
    Task<IReadOnlyList<CaptureRange>> GetCaptureRangesAsync(long profileId);
    Task<long> SaveCaptureRangeAsync(long profileId, string name, CapturePosition start, CapturePosition end);
    Task<bool> AppendCaptureAsync(long sessionId, CaptureReceived received, CaptureRules rules);
    Task<IReadOnlyList<CaptureTextItem>> GetCaptureItemsAsync(long sessionId, int limit = 200, int offset = 0, long? labelId = null, bool unclassifiedOnly = false, bool latest = false);
    Task SetCaptureLabelsAsync(long sessionId, IReadOnlyCollection<long> itemIds, long? labelId);
    Task SetCaptureSpeakerAsync(long sessionId, long itemId, string? speaker);
    Task SplitCaptureEventAsync(long sessionId, long sequence, IReadOnlyList<int> boundaries, IReadOnlyList<CaptureManualPart>? parts = null);
    Task ReapplyCaptureRulesAsync(long sessionId, IReadOnlyCollection<long> itemIds, CaptureRules rules);
    Task ResolveCaptureSpeakersAsync(long sessionId, DateTimeOffset since, DateTimeOffset now, IReadOnlyCollection<long>? sequences = null);
}
