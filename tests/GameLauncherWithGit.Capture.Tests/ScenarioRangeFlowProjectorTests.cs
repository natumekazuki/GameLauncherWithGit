using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioRangeFlowProjectorTests
{
    private const long Record = 71;

    /// <summary>Twenty observed rows between two saved ranges are one node, not twenty text nodes.</summary>
    [Fact]
    public void ConsecutiveUnconfiguredRowsCollapseBetweenSavedRanges()
    {
        var parts = Enumerable.Range(1, 22).Select(seq => Part(1, seq, $"row{seq}")).ToArray();
        var graph = Project(parts, [Range(7, "序章", parts[0]), Range(9, "再会", parts[^1])]);
        var chart = Assert.Single(graph.Charts);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(graph.Nodes.Select(n => n.Id), chart.NodeIds);
        var unset = Assert.Single(graph.Nodes, node => node.SavedRangeId is null);
        Assert.Equal("未設定区間", unset.Name);
        Assert.Equal(parts[1].Start, unset.Start);
        Assert.Equal(parts[^2].End, unset.End);
        Assert.Equal(2, graph.Edges.Count);
        AssertEdge(graph, graph.Nodes[0], unset);
        AssertEdge(graph, unset, graph.Nodes[2]);
    }

    /// <summary>Whole corresponding runs may share a saved node; branch and join edges still require real visits.</summary>
    [Fact]
    public void CompleteRevisitsBuildAnObservedBranchAndJoinWithoutMatchingNames()
    {
        var first = new[] { Part(1, 1, "shared-a"), Part(1, 2, "shared-b"), Part(1, 3, "branch-x"), Part(1, 4, "shared-z") };
        var second = new[] { Part(2, 1, "shared-a"), Part(2, 2, "shared-b"), Part(2, 3, "branch-y"), Part(2, 4, "shared-z") };
        var third = new[] { Part(3, 1, "shared-a"), Part(3, 2, "diverges") };
        var ranges = new[]
        {
            Range(1, "序章", first[0], first[1]), Range(2, "同名", first[2]),
            Range(3, "同名", second[2]), Range(4, "合流", first[3])
        };
        var graph = Project([.. first, .. second, .. third], ranges);
        Assert.Single(graph.Charts);
        var a = Assert.Single(graph.Nodes, n => n.SavedRangeId == 1);
        var x = Assert.Single(graph.Nodes, n => n.SavedRangeId == 2);
        var y = Assert.Single(graph.Nodes, n => n.SavedRangeId == 3);
        var z = Assert.Single(graph.Nodes, n => n.SavedRangeId == 4);
        Assert.Equal(x.Name, y.Name);
        Assert.NotEqual(x.Id, y.Id);
        Assert.Equal(4, graph.Edges.Count);
        AssertEdge(graph, a, x);
        AssertEdge(graph, a, y);
        AssertEdge(graph, x, z);
        AssertEdge(graph, y, z);
        var incomplete = Assert.Single(graph.Nodes, n => n.SavedRangeId is null);
        Assert.Equal((third[0].Start, third[^1].End), (incomplete.Start, incomplete.End));
        Assert.DoesNotContain(graph.Edges, e => e.FromId == incomplete.Id || e.ToId == incomplete.Id);

        var renamed = Project([.. first, .. second, .. third], ranges.Select(r => r with { Name = "変更後" }).ToArray());
        Assert.Equal(graph.Nodes.Select(n => n.Id), renamed.Nodes.Select(n => n.Id));
        Assert.Equal(graph.Edges, renamed.Edges);
    }

    /// <summary>Two saved ranges that both explain a third visit do not arbitrarily acquire its outgoing or incoming edges.</summary>
    [Fact]
    public void CompetingCompleteRevisitsRemainAnUnconfiguredTrace()
    {
        var sourceA = Part(1, 1, "canonical");
        var sourceB = Part(2, 1, "canonical");
        var third = new[] { Part(3, 1, "pre"), Part(3, 2, "canonical"), Part(3, 3, "post") };
        var graph = Project([sourceA, sourceB, .. third], [Range(1, "候補A", sourceA), Range(2, "候補B", sourceB)]);
        Assert.Single(graph.Charts);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Empty(graph.Edges);
        var n = Assert.Single(graph.Nodes, x => x.SavedRangeId is null);
        Assert.Equal((third[0].Start, third[2].End), (n.Start, n.End));
    }

    /// <summary>Overlapping saved ranges stay separately selectable but the overlap cannot justify a transition.</summary>
    [Fact]
    public void OverlappingSpansDoNotInventAnEdgeOrMergeEqualNames()
    {
        var parts = Enumerable.Range(1, 7).Select(seq => Part(1, seq, $"row{seq}")).ToArray();
        var graph = Project(parts, [Range(1, "同名", parts[1], parts[2], parts[3]), Range(2, "同名", parts[3], parts[4], parts[5])]);
        var a = Assert.Single(graph.Nodes, n => n.SavedRangeId == 1);
        var b = Assert.Single(graph.Nodes, n => n.SavedRangeId == 2);
        var before = Assert.Single(graph.Nodes, n => n.SavedRangeId is null && n.Start == parts[0].Start);
        var after = Assert.Single(graph.Nodes, n => n.SavedRangeId is null && n.Start == parts[6].Start);
        Assert.Equal(2, graph.Edges.Count);
        AssertEdge(graph, before, a);
        AssertEdge(graph, b, after);
    }

    /// <summary>Physical cuts and independent regions partition a saved range; neither gap is bridged.</summary>
    [Fact]
    public void CutsAndIndependentChartsPartitionSavedAndUnsetIntervals()
    {
        var parts = Enumerable.Range(1, 7).Select(seq => Part(1, seq, $"row{seq}")).ToArray();
        var options = new ScenarioFlowOverrides([parts[2].Start], [new(4, parts[4].Start, parts[5].End)]);
        var graph = Project(parts, [Range(4, "横断する保存範囲", parts[1], parts[2], parts[3], parts[4], parts[5])], options);
        Assert.Equal(4, graph.Charts.Count);
        var saved = graph.Nodes.Where(n => n.SavedRangeId == 4).ToArray();
        Assert.Equal(3, saved.Length);
        Assert.Equal(3, saved.Select(n => n.Id).Distinct().Count());
        Assert.Contains(saved, n => n.Start == parts[1].Start && n.End == parts[1].End);
        Assert.Contains(saved, n => n.Start == parts[2].Start && n.End == parts[3].End);
        Assert.Contains(saved, n => n.Start == parts[4].Start && n.End == parts[5].End);
        var onlyEdge = Assert.Single(graph.Edges);
        Assert.Equal(parts[0].Start, graph.Nodes.Single(n => n.Id == onlyEdge.FromId).Start);
        Assert.Equal(parts[1].Start, graph.Nodes.Single(n => n.Id == onlyEdge.ToId).Start);
    }

    /// <summary>Each separately observed chain becomes one unsaved node even when their canonical blocks share a chart.</summary>
    [Fact]
    public void ZeroSavedRangesCoalescesEachTraceAndDoesNotSynthesizeAnEmptyChart()
    {
        var a = Part(1, 1, "a");
        var b = Part(1, 2, "b");
        var cut = Part(1, 3, "a");
        var d = Part(1, 4, "d");
        var emptyRange = new ScenarioMaterialRange("4", "本文なし", a.Start, d.End, [], [], [], 0);
        var graph = Project([a, b, cut, d], [emptyRange], new([cut.Start], []));
        Assert.Single(graph.Charts);
        Assert.Equal(new[] { (a.Start, b.End), (cut.Start, d.End) }, graph.Nodes.Select(x => (x.Start, x.End)));
        Assert.All(graph.Nodes, n => Assert.Null(n.SavedRangeId));
        Assert.Empty(graph.Edges);
        Assert.Empty(Project([], [emptyRange]).Charts);
    }

    /// <summary>Within a single occurrence only the saved UTF-16 slice is named; surrounding unsaved pieces remain distinct.</summary>
    [Fact]
    public void PartialSavedSliceUsesExactHalfOpenBoundaries()
    {
        var part = Part(1, 4, "single", "🌱あいうえ");
        var start = part.Start with { Offset = 2 };
        var end = part.End with { Offset = 5 };
        var graph = Project([part], [new("19", "内側", start, end, [], [new(Record, 1, 4, 2, 3)], [], 0)]);
        Assert.Equal(new[] { (part.Start, start), (start, end), (end, part.End) },
            graph.Nodes.Select(x => (x.Start, x.End)));
        Assert.Equal(new long?[] { null, 19, null }, graph.Nodes.Select(x => x.SavedRangeId));
        AssertEdge(graph, graph.Nodes[0], graph.Nodes[1]);
        AssertEdge(graph, graph.Nodes[1], graph.Nodes[2]);
    }

    /// <summary>Literal equality and chronological adjacency without a traversal cannot supplement the graph.</summary>
    [Fact]
    public void MissingTraversalIsNotReplacedByBlockAdjacency()
    {
        var first = Part(1, 1, "distinct-a", "同文");
        var second = Part(1, 2, "distinct-b", "同文");
        var native = ScenarioFlowBuilder.Build([first, second], new([], []));
        var graph = ScenarioRangeFlowProjector.Build(new(new(Record, 1, 1), true, [], [], [])
        {
            Graph = native with { Edges = native.Edges.Select(edge => edge with { Traversals = [] }).ToArray() },
            Ranges = [Range(1, "保存", first)]
        });
        Assert.Single(graph.Charts);
        Assert.Equal(2, graph.Nodes.Count);
        Assert.Empty(graph.Edges);
        Assert.Equal(second.Start, Assert.Single(graph.Nodes, n => n.SavedRangeId is null).Start);
    }

    /// <summary>Back-to-back full visits can loop, but a cut between the same confirmed blocks breaks that evidence.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteRepeatLoopsOnlyWhenBothVisitsAreInTheSameTrace(bool cut)
    {
        var first = new[] { Part(1, 1, "a"), Part(1, 2, "b") };
        var second = new[] { Part(1, 3, "a"), Part(1, 4, "b") };
        var graph = Project([.. first, .. second], [Range(8, "保存", first)],
            new(cut ? [second[0].Start] : [], []));
        var saved = Assert.Single(graph.Nodes, node => node.SavedRangeId == 8);
        if (!cut)
        {
            Assert.Single(graph.Nodes);
            Assert.Equal(new ScenarioRangeFlowEdge(saved.Id, saved.Id), Assert.Single(graph.Edges));
        }
        else
        {
            Assert.Empty(graph.Edges);
            Assert.Equal(second[0].Start, Assert.Single(graph.Nodes, n => n.SavedRangeId is null).Start);
        }
    }

    /// <summary>Creates a recorded fragment, using the same block ID only for a confirmed correspondence in the fixture.</summary>
    private static ScenarioFlowPart Part(long session, long seq, string block, string text = "合成本文") =>
        new(block, new(session, seq, 0), new(session, seq, text.Length), text, null);

    /// <summary>Builds saved source spans as the material adapter would for these whole synthetic fragments.</summary>
    private static ScenarioMaterialRange Range(long id, string name, params ScenarioFlowPart[] parts) =>
        new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), name, parts[0].Start, parts[^1].End, [],
            parts.Select(part => new ScenarioOccurrenceRef(Record, part.Start.SessionId, part.Start.Sequence,
                part.Start.Offset, part.End.Offset - part.Start.Offset)).ToArray(), [], 0);

    /// <summary>Uses the production flow builder so chart, cut, and traversal semantics are genuine.</summary>
    private static ScenarioRangeFlowGraph Project(IReadOnlyList<ScenarioFlowPart> parts, IReadOnlyList<ScenarioMaterialRange> ranges,
        ScenarioFlowOverrides? options = null) =>
        ScenarioRangeFlowProjector.Build(new(new(Record, 1, 1), true, [], [], [])
        { Graph = ScenarioFlowBuilder.Build(parts, options ?? new([], [])), Ranges = ranges });

    /// <summary>Checks the actual directed edge without depending on ID serialization.</summary>
    private static void AssertEdge(ScenarioRangeFlowGraph graph, ScenarioRangeFlowNode from, ScenarioRangeFlowNode to) =>
        Assert.Contains(new ScenarioRangeFlowEdge(from.Id, to.Id), graph.Edges);
}
