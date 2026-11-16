namespace GameLauncherWithGit.Application.Models;

/// <summary>Raw positions survive classification and item re-splitting.</summary>
public sealed record ScenarioFlowRegion(long Id, CapturePosition Start, CapturePosition End);
public sealed record ScenarioFlowOverrides(IReadOnlyList<CapturePosition> Cuts, IReadOnlyList<ScenarioFlowRegion> Regions);
public sealed record ScenarioFlowPart(string BlockId, CapturePosition Start, CapturePosition End,
    string Text, string? Speaker);

public sealed record ScenarioFlowOccurrence(string Id, string BlockId, string ChartId,
    CapturePosition Start, CapturePosition End, string Text, string? Speaker, bool CutBefore);
public sealed record ScenarioFlowBlock(string Id, string Text, IReadOnlyList<string> OccurrenceIds);
public sealed record ScenarioFlowEdge(string FromBlockId, string ToBlockId, IReadOnlyList<ScenarioFlowTraversal> Traversals);
public sealed record ScenarioFlowTraversal(string FromOccurrenceId, string ToOccurrenceId, long SessionId);
public sealed record ScenarioFlowChart(string Id, IReadOnlyList<string> OccurrenceIds);
public sealed record ScenarioFlowGraph(IReadOnlyList<ScenarioFlowChart> Charts, IReadOnlyList<ScenarioFlowBlock> Blocks,
    IReadOnlyList<ScenarioFlowOccurrence> Occurrences, IReadOnlyList<ScenarioFlowEdge> Edges);
