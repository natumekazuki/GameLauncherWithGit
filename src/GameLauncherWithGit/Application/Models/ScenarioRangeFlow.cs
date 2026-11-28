namespace GameLauncherWithGit.Application.Models;

/// <summary>An observed range diagram without the underlying text or occurrences.</summary>
public sealed record ScenarioRangeFlowGraph(IReadOnlyList<ScenarioRangeFlowChart> Charts,
    IReadOnlyList<ScenarioRangeFlowNode> Nodes, IReadOnlyList<ScenarioRangeFlowEdge> Edges);

/// <summary>One observed chart, with the IDs of its range nodes in display order.</summary>
public sealed record ScenarioRangeFlowChart(string Id, IReadOnlyList<string> NodeIds);

/// <summary>A saved range or an unsaved observed interval. Start is inclusive and End is exclusive.</summary>
public sealed record ScenarioRangeFlowNode(string Id, string Name, long? SavedRangeId, CapturePosition Start, CapturePosition End);

/// <summary>A directed transition supported by observed traversal in one chart.</summary>
public sealed record ScenarioRangeFlowEdge(string FromId, string ToId);
