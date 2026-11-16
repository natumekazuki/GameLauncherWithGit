namespace GameLauncherWithGit.Application.Models;

/// <summary>A source position, valid only with the material version that resolved it.</summary>
public sealed record ScenarioOccurrenceRef(long CaptureGameId, long SessionId, long ReceiveSequence,
    int StartUtf16, int LengthUtf16);

public sealed record ScenarioMaterialVersion(long CaptureGameId, long Revision, long MatchingRevision);
public sealed record ScenarioContent(string Id, string Text, CaptureKind? Kind);
public sealed record ScenarioOccurrence(string Id, ScenarioOccurrenceRef Source, string ContentId,
    string? Speaker, long? LabelId, string? LabelName, DateTimeOffset ReceivedAt,
    IReadOnlyList<ScenarioOccurrenceImage> Images);
public sealed record ScenarioOccurrenceImage(string ImageId, DateTimeOffset CapturedAt);
/// <summary>Selected occurrence pairs only; no representative outside the selection is exposed.</summary>
public sealed record ScenarioMatch(string Id, IReadOnlyList<ScenarioOccurrencePair> Items);
public sealed record ScenarioOccurrencePair(ScenarioOccurrenceRef Representative, ScenarioOccurrenceRef Revisit);
public sealed record ScenarioMaterialSnapshot(ScenarioMaterialVersion Version, bool MatchingComplete,
    IReadOnlyList<ScenarioContent> Contents, IReadOnlyList<ScenarioOccurrence> Occurrences,
    IReadOnlyList<ScenarioMatch> Matches)
{
    public IReadOnlyList<ScenarioMaterialRange> Ranges { get; init; } = [];
    public IReadOnlyList<ScenarioMaterialClassification> Classifications { get; init; } = [];
    public IReadOnlyList<ScenarioMaterialImageMetadata> Images { get; init; } = [];
    public ScenarioFlowGraph Graph { get; init; } = new([], [], [], []);
}
public sealed record ScenarioMaterialRange(string Id, string Name, CapturePosition Start, CapturePosition End,
    IReadOnlyList<string> Tags, IReadOnlyList<ScenarioOccurrenceRef> Spans);
public sealed record ScenarioMatchingBatchResult(int ProcessedItems, bool HasMore, bool DeferredForCapture);
