using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using System.Globalization;

namespace GameLauncherWithGit.Infrastructure.Services;

/// <summary>Permission-independent identifiers and explicit content allowlists for MCP and archives.</summary>
internal static class ScenarioMaterialWireAdapter
{
    public static ScenarioFullProjection ToWire(ScenarioMaterialSnapshot snapshot) =>
        (ScenarioFullProjection)ToWire(snapshot, ScenarioMaterialAccessLevel.Full);

    public static ScenarioMaterialProjection ToWire(ScenarioMaterialSnapshot snapshot, ScenarioMaterialAccessLevel level)
    {
        var version = ToWire(snapshot.Version);
        var selection = SelectionVersion(snapshot);
        var ranges = snapshot.Ranges.Select(x => new ScenarioMaterialWireRange(x.Id, x.Name, ToWire(x.Start), ToWire(x.End),
            x.Tags, x.Spans.Select(ToWire).ToArray())).ToArray();
        var matches = snapshot.Matches.Select(match => new ScenarioMaterialWireMatch(match.Id,
            match.Items.Select(pair => new ScenarioMaterialWirePair(ToWire(pair.Representative), ToWire(pair.Revisit))).ToArray())).ToArray();
        var game = snapshot.Version.CaptureGameId;
        var graph = snapshot.Graph;
        var sources = graph.Occurrences.ToDictionary(x => x.Id, x =>
            new ScenarioOccurrenceRef(game, x.Start.SessionId, x.Start.Sequence, x.Start.Offset, x.End.Offset - x.Start.Offset));
        var occurrenceIds = sources.ToDictionary(x => x.Key, x => ScenarioMaterialIds.Occurrence(x.Value));
        var charts = graph.Charts.Select(x => new ScenarioFlowChart(ScenarioMaterialIds.Chart(game, x.Id),
            x.OccurrenceIds.Select(id => occurrenceIds[id]).ToArray())).ToArray();
        var edges = graph.Edges.Select(x => new ScenarioFlowWireEdge(ScenarioMaterialIds.Node(game, x.FromBlockId),
            ScenarioMaterialIds.Node(game, x.ToBlockId), x.Traversals.Select(t => new ScenarioFlowWireTraversal(
                occurrenceIds[t.FromOccurrenceId], occurrenceIds[t.ToOccurrenceId], Identifier(t.SessionId))).ToArray())).ToArray();
        IReadOnlyList<ScenarioMaterialWireImage> Images(ScenarioOccurrence x) =>
            x.Images.Select(image => new ScenarioMaterialWireImage(image.ImageId, image.CapturedAt)).ToArray();
        return level switch
        {
            ScenarioMaterialAccessLevel.Structure => new ScenarioStructureProjection(version, selection, snapshot.MatchingComplete,
                snapshot.Contents.Select(x => new ScenarioStructureContent(x.Id, KindName(x.Kind))).ToArray(),
                snapshot.Occurrences.Select(x => new ScenarioStructureOccurrence(x.Id, ToWire(x.Source), x.ContentId,
                    Identifier(x.LabelId), x.LabelName, x.ReceivedAt, Images(x))).ToArray(),
                matches, ranges, snapshot.Classifications, snapshot.Images,
                new(charts, graph.Blocks.Select(x => new ScenarioStructureFlowBlock(ScenarioMaterialIds.Node(game, x.Id),
                        x.OccurrenceIds.Select(id => occurrenceIds[id]).ToArray())).ToArray(),
                    graph.Occurrences.Select(x => new ScenarioStructureFlowOccurrence(occurrenceIds[x.Id],
                        ScenarioMaterialIds.Node(game, x.BlockId), ScenarioMaterialIds.Chart(game, x.ChartId), ToWire(sources[x.Id]), x.CutBefore)).ToArray(), edges)),
            ScenarioMaterialAccessLevel.Full => new ScenarioFullProjection(version, selection, snapshot.MatchingComplete,
                snapshot.Contents.Select(x => new ScenarioMaterialWireContent(x.Id, x.Text, KindName(x.Kind))).ToArray(),
                snapshot.Occurrences.Select(x => new ScenarioMaterialWireOccurrence(x.Id, ToWire(x.Source), x.ContentId,
                    x.Speaker, Identifier(x.LabelId), x.LabelName, x.ReceivedAt, Images(x))).ToArray(),
                matches, ranges, snapshot.Classifications, snapshot.Images,
                new(charts, graph.Blocks.Select(x => new ScenarioFlowBlock(ScenarioMaterialIds.Node(game, x.Id), x.Text,
                        x.OccurrenceIds.Select(id => occurrenceIds[id]).ToArray())).ToArray(),
                    graph.Occurrences.Select(x => new ScenarioFullFlowOccurrence(occurrenceIds[x.Id],
                        ScenarioMaterialIds.Node(game, x.BlockId), ScenarioMaterialIds.Chart(game, x.ChartId), ToWire(sources[x.Id]), x.CutBefore, x.Text, x.Speaker)).ToArray(), edges)),
            _ => throw new ArgumentOutOfRangeException(nameof(level))
        };
    }

    public static ScenarioMaterialSelectionVersion SelectionVersion(ScenarioMaterialSnapshot snapshot) =>
        new(snapshot.Occurrences.Select(x => x.Id).ToArray(), snapshot.Ranges.Select(x => x.Id).ToArray());

    public static ScenarioMaterialWireVersion ToWire(ScenarioMaterialVersion version) => new(
        Identifier(version.CaptureGameId), version.Revision, version.MatchingRevision);
    private static ScenarioMaterialWireReference ToWire(ScenarioOccurrenceRef reference) => new(
        Identifier(reference.CaptureGameId), Identifier(reference.SessionId), reference.ReceiveSequence, reference.StartUtf16, reference.LengthUtf16);
    private static ScenarioMaterialWirePosition ToWire(CapturePosition position) => new(Identifier(position.SessionId), position.Sequence, position.Offset);
    private static string Identifier(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? Identifier(long? value) => value.HasValue ? Identifier(value.Value) : null;
    public static string? KindName(CaptureKind? kind) => kind switch
    {
        CaptureKind.Dialogue => nameof(CaptureKind.Dialogue),
        CaptureKind.Narration => nameof(CaptureKind.Narration),
        CaptureKind.Other => nameof(CaptureKind.Other),
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown capture kind cannot be written to a scenario projection.")
    };
}
