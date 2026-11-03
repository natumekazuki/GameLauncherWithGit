using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IScenarioCaptureStore
{
    Task<CaptureProfile> GetOrCreateCaptureProfileAsync(string gameId);
    Task<IReadOnlyList<CaptureProfile>> GetCaptureProfilesAsync();
    Task SaveCaptureProfileAsync(CaptureProfile profile);
    Task<IReadOnlyList<CaptureLabel>> GetCaptureLabelsAsync(long profileId);
    Task<long> SaveCaptureLabelAsync(long profileId, string name, CaptureKind kind, long? labelId = null);
    Task<long> BeginCaptureSessionAsync(long profileId);
    Task EndCaptureSessionAsync(long sessionId);
    Task<IReadOnlyList<CaptureSession>> GetCaptureSessionsAsync(long profileId);
    Task<bool> AppendCaptureAsync(long sessionId, CaptureReceived received, CaptureRules rules);
    Task<IReadOnlyList<CaptureTextItem>> GetCaptureItemsAsync(long sessionId, int limit = 200, int offset = 0, long? labelId = null, bool unclassifiedOnly = false, bool latest = false);
    Task SetCaptureLabelsAsync(long sessionId, IReadOnlyCollection<long> itemIds, long? labelId);
    Task SetCaptureSpeakerAsync(long sessionId, long itemId, string? speaker);
    Task SplitCaptureEventAsync(long sessionId, long sequence, IReadOnlyList<int> boundaries, IReadOnlyList<CaptureManualPart>? parts = null);
    Task ReapplyCaptureRulesAsync(long sessionId, IReadOnlyCollection<long> itemIds, CaptureRules rules);
    Task ResolveCaptureSpeakersAsync(long sessionId, CaptureRules rules, DateTimeOffset since, DateTimeOffset now, IReadOnlyCollection<long>? sequences = null);
}
