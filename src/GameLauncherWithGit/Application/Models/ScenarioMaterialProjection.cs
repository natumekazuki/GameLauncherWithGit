using System.Text.Json.Serialization;

namespace GameLauncherWithGit.Application.Models;

public enum ScenarioMaterialAccessLevel { Structure, Full }

/// <summary>Identifies the selected result set without revealing text or speaker search terms.</summary>
public sealed record ScenarioMaterialSelectionVersion(IReadOnlyList<string> OccurrenceIds, IReadOnlyList<string> RangeIds)
{
    public bool SameSelection(ScenarioMaterialSelectionVersion other) =>
        OccurrenceIds.SequenceEqual(other.OccurrenceIds, StringComparer.Ordinal)
        && RangeIds.SequenceEqual(other.RangeIds, StringComparer.Ordinal);
}

[JsonDerivedType(typeof(ScenarioStructureProjection))]
[JsonDerivedType(typeof(ScenarioFullProjection))]
public abstract record ScenarioMaterialProjection(ScenarioMaterialWireVersion Version,
    ScenarioMaterialSelectionVersion SelectionVersion, bool MatchingComplete);

public sealed record ScenarioStructureProjection(ScenarioMaterialWireVersion Version,
    ScenarioMaterialSelectionVersion SelectionVersion, bool MatchingComplete,
    IReadOnlyList<ScenarioStructureContent> Contents, IReadOnlyList<ScenarioStructureOccurrence> Occurrences,
    IReadOnlyList<ScenarioMaterialWireMatch> Matches, IReadOnlyList<ScenarioMaterialWireRange> Ranges,
    IReadOnlyList<ScenarioMaterialClassification> Classifications, IReadOnlyList<ScenarioMaterialImageMetadata> Images,
    ScenarioStructureFlow Graph) : ScenarioMaterialProjection(Version, SelectionVersion, MatchingComplete);

public sealed record ScenarioFullProjection(ScenarioMaterialWireVersion Version,
    ScenarioMaterialSelectionVersion SelectionVersion, bool MatchingComplete,
    IReadOnlyList<ScenarioMaterialWireContent> Contents, IReadOnlyList<ScenarioMaterialWireOccurrence> Occurrences,
    IReadOnlyList<ScenarioMaterialWireMatch> Matches, IReadOnlyList<ScenarioMaterialWireRange> Ranges,
    IReadOnlyList<ScenarioMaterialClassification> Classifications, IReadOnlyList<ScenarioMaterialImageMetadata> Images,
    ScenarioFullFlow Graph) : ScenarioMaterialProjection(Version, SelectionVersion, MatchingComplete);

public sealed record ScenarioMaterialWireVersion(string CaptureGameId, long Revision, long MatchingRevision);
public sealed record ScenarioStructureContent(string Id, string? Kind);
public sealed record ScenarioMaterialWireContent(string Id, string Text, string? Kind);
public sealed record ScenarioStructureOccurrence(string Id, ScenarioMaterialWireReference Source, string ContentId,
    string? LabelId, string? LabelName, DateTimeOffset ReceivedAt, IReadOnlyList<ScenarioMaterialWireImage> Images);
public sealed record ScenarioMaterialWireOccurrence(string Id, ScenarioMaterialWireReference Source, string ContentId,
    string? Speaker, string? LabelId, string? LabelName, DateTimeOffset ReceivedAt, IReadOnlyList<ScenarioMaterialWireImage> Images);
public sealed record ScenarioMaterialWireReference(string CaptureGameId, string SessionId, long ReceiveSequence, int StartUtf16, int LengthUtf16);
public sealed record ScenarioMaterialWirePosition(string SessionId, long ReceiveSequence, int OffsetUtf16);
public sealed record ScenarioMaterialWireImage(string ImageId, DateTimeOffset CapturedAt);
public sealed record ScenarioMaterialWireMatch(string Id, IReadOnlyList<ScenarioMaterialWirePair> Items);
public sealed record ScenarioMaterialWirePair(ScenarioMaterialWireReference Representative, ScenarioMaterialWireReference Revisit);
public sealed record ScenarioMaterialWireRange(string Id, string Name, ScenarioMaterialWirePosition Start,
    ScenarioMaterialWirePosition End, IReadOnlyList<string> Tags, IReadOnlyList<ScenarioMaterialWireReference> Spans);
public sealed record ScenarioMaterialClassification(string Id, string Name, string? Kind);
public sealed record ScenarioMaterialImageMetadata(string Id, int Width, int Height, string PixelHash);

public sealed record ScenarioStructureFlow(IReadOnlyList<ScenarioFlowChart> Charts,
    IReadOnlyList<ScenarioStructureFlowBlock> Blocks, IReadOnlyList<ScenarioStructureFlowOccurrence> Occurrences,
    IReadOnlyList<ScenarioFlowWireEdge> Edges);
public sealed record ScenarioFullFlow(IReadOnlyList<ScenarioFlowChart> Charts,
    IReadOnlyList<ScenarioFlowBlock> Blocks, IReadOnlyList<ScenarioFullFlowOccurrence> Occurrences,
    IReadOnlyList<ScenarioFlowWireEdge> Edges);
public sealed record ScenarioFlowWireEdge(string FromBlockId, string ToBlockId, IReadOnlyList<ScenarioFlowWireTraversal> Traversals);
public sealed record ScenarioFlowWireTraversal(string FromOccurrenceId, string ToOccurrenceId, string SessionId);
public sealed record ScenarioStructureFlowBlock(string Id, IReadOnlyList<string> OccurrenceIds);
public sealed record ScenarioStructureFlowOccurrence(string Id, string BlockId, string ChartId,
    ScenarioMaterialWireReference Source, bool CutBefore);
public sealed record ScenarioFullFlowOccurrence(string Id, string BlockId, string ChartId,
    ScenarioMaterialWireReference Source, bool CutBefore, string Text, string? Speaker);

/// <summary>Image reads must revalidate this selection, version and exact occurrence/image association under the image gate.</summary>
public sealed record ScenarioMaterialImageRequest(long CaptureGameId, ScenarioArchiveSelection Selection,
    ScenarioMaterialWireVersion Version, ScenarioMaterialSelectionVersion SelectionVersion, string OccurrenceId, string ImageId);
