using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Application.Services;

/// <summary>Identifies a committed capture update without exposing any of its text.</summary>
public sealed record ScenarioLiveFeedVersion(long RecordId, long SessionId, long ScopeVersion, long Revision)
{
    /// <summary>Checks the record, session and scope displayed by a reader.</summary>
    public bool Matches(long? recordId, long? sessionId, long scopeVersion) =>
        RecordId == recordId && SessionId == sessionId && ScopeVersion == scopeVersion;
}

public sealed partial class ScenarioCaptureService
{
    private ScenarioLiveFeedVersion? _liveFeedVersion;
    private ScenarioLiveFeedVersion? _liveFeedSpeakerVersion;

    /// <summary>The newest committed body version, also available if a subscriber skipped a wakeup.</summary>
    public ScenarioLiveFeedVersion? LiveFeedVersion => Volatile.Read(ref _liveFeedVersion);

    /// <summary>Tracks delayed automatic speaker writes separately from new body arrivals.</summary>
    internal ScenarioLiveFeedVersion? LiveFeedSpeakerVersion => Volatile.Read(ref _liveFeedSpeakerVersion);

    /// <summary>Signals a committed body. Subscribers schedule their work without blocking the capture writer.</summary>
    public event Action<ScenarioLiveFeedVersion>? LiveFeedCommitted;

    /// <summary>Advances the version from the single writer and wakes each reader independently.</summary>
    private void PublishLiveFeed(long recordId, long sessionId, long scopeVersion)
    {
        var version = new ScenarioLiveFeedVersion(recordId, sessionId, scopeVersion, (_liveFeedVersion?.Revision ?? 0) + 1);
        Volatile.Write(ref _liveFeedVersion, version);
        if (LiveFeedCommitted is not { } listeners) return;

        // One disposed or faulty reader must not affect a saved transaction or another reader.
        foreach (Action<ScenarioLiveFeedVersion> listener in listeners.GetInvocationList())
        {
            try { listener(version); }
            catch (Exception ex)
            {
                _logger.LogWarning("Capture feed notification failed ({ExceptionType}).", ex.GetType().Name);
            }
        }
    }

    /// <summary>Publishes a committed speaker batch under the capture state gate.</summary>
    private void PublishLiveFeedSpeakers(long recordId, long sessionId, long scopeVersion) =>
        Volatile.Write(ref _liveFeedSpeakerVersion,
            new(recordId, sessionId, scopeVersion, (_liveFeedSpeakerVersion?.Revision ?? 0) + 1));
}
