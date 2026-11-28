using GameLauncherWithGit.Application.Models;
using System.Globalization;

namespace GameLauncherWithGit.Application.Services;

/// <summary>Projects only observed transitions and bounded text ranges into a display graph.</summary>
public static class ScenarioRangeFlowProjector
{
    private const string UnsetRangeName = "未設定区間";

    private sealed record SavedRange(string Name, CapturePosition Start, CapturePosition End, long Length);
    private sealed record Span(long RangeId, int Start, int End);

    private sealed class Slice(ScenarioFlowOccurrence occurrence, int start, int end, long[] rangeIds)
    {
        public ScenarioFlowOccurrence Occurrence { get; } = occurrence;
        public CapturePosition Start { get; } = occurrence.Start with { Offset = start };
        public CapturePosition End { get; } = occurrence.Start with { Offset = end };
        public long[] RangeIds { get; } = rangeIds;
        public ScenarioRangeFlowNode? Node { get; set; }
        public CapturePosition? VisitStart { get; set; }
    }

    private sealed class Trace(string chartId, List<ScenarioFlowOccurrence> occurrences, List<Slice> slices)
    {
        public string ChartId { get; } = chartId;
        public List<ScenarioFlowOccurrence> Occurrences { get; } = occurrences;
        public List<Slice> Slices { get; } = slices;
        public List<SavedVisit> Visits { get; } = [];
        public List<Revisit> Repeats { get; } = [];
    }

    private sealed class SavedVisit(Trace trace, long rangeId, int first)
    {
        public Trace Trace { get; } = trace;
        public long RangeId { get; } = rangeId;
        public int First { get; } = first;
        public int Last { get; set; } = first;
        public CapturePosition Start => Trace.Slices[First].Start;
        public CapturePosition End => Trace.Slices[Last].End;
        public ScenarioRangeFlowNode Node { get; set; } = default!;
    }

    private sealed record PatternPart(string BlockId, int StartInBlock, int EndInBlock, int BlockLength);
    private sealed record Revisit(CapturePosition Start, CapturePosition End, ScenarioRangeFlowNode Node);
    private readonly record struct Piece(CapturePosition Start, CapturePosition End, ScenarioRangeFlowNode? Node,
        CapturePosition? VisitStart = null, bool Ambiguous = false);

    /// <summary>Uses the material snapshot's already-resolved traversals; the output cannot carry text or image data.</summary>
    public static ScenarioRangeFlowGraph Build(ScenarioMaterialSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var graph = snapshot.Graph;
        var ranges = new Dictionary<long, SavedRange>();
        var spansByEvent = new Dictionary<CaptureEventKey, List<Span>>();
        // The selected material's intersections, not a range's enclosing dates, establish membership.
        foreach (var range in snapshot.Ranges)
        {
            if (!long.TryParse(range.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0) continue;
            var length = 0L;
            foreach (var span in range.Spans.Distinct())
            {
                if (span.CaptureGameId != snapshot.Version.CaptureGameId || span.LengthUtf16 <= 0 || span.StartUtf16 < 0) continue;
                var start = new CapturePosition(span.SessionId, span.ReceiveSequence, span.StartUtf16);
                var end = start with { Offset = checked(span.StartUtf16 + span.LengthUtf16) };
                if (start.CompareTo(range.Start) < 0) start = range.Start;
                if (end.CompareTo(range.End) > 0) end = range.End;
                if (start.CompareTo(end) >= 0) continue;
                var key = new CaptureEventKey(span.SessionId, span.ReceiveSequence);
                if (!spansByEvent.TryGetValue(key, out var spans)) spansByEvent[key] = spans = [];
                spans.Add(new(id, start.Offset, end.Offset));
                length += end.Offset - start.Offset;
            }
            if (length > 0) ranges.Add(id, new(range.Name, range.Start, range.End, length));
        }

        var traces = ReadTraces(graph, spansByEvent).ToArray();
        foreach (var trace in traces) FindSavedVisits(trace);
        var allVisits = traces.SelectMany(x => x.Visits).ToArray();
        var nodesByChart = new Dictionary<string, List<ScenarioRangeFlowNode>>();
        List<ScenarioRangeFlowNode> ChartNodes(string chartId)
        {
            if (!nodesByChart.TryGetValue(chartId, out var result)) nodesByChart[chartId] = result = [];
            return result;
        }
        foreach (var group in allVisits.GroupBy(x => (x.Trace.ChartId, x.RangeId)))
        {
            var partitioned = group.Count() > 1;
            foreach (var visit in group)
            {
                var prefix = FormattableString.Invariant($"{group.Key.ChartId}/range:{visit.RangeId}");
                var id = partitioned ? $"{prefix}/{PositionId(visit.Start)}" : prefix;
                visit.Node = new(id, ranges[visit.RangeId].Name, visit.RangeId, visit.Start, visit.End);
                ChartNodes(group.Key.ChartId).Add(visit.Node);
                // An overlapping slice can belong to multiple selectable saved nodes, but not to a directed edge.
                for (var i = visit.First; i <= visit.Last; i++)
                {
                    var slice = visit.Trace.Slices[i];
                    if (slice.RangeIds.Length != 1) continue;
                    slice.Node = visit.Node;
                    slice.VisitStart = visit.Start;
                }
            }
        }

        AddCompleteRevisits(traces, allVisits, ranges);
        var edges = new HashSet<ScenarioRangeFlowEdge>();
        foreach (var trace in traces) ConnectTrace(trace, ChartNodes(trace.ChartId), edges);
        var chartNodes = graph.Charts.Where(chart => nodesByChart.TryGetValue(chart.Id, out var nodes) && nodes.Count > 0)
            .Select(chart => (chart.Id, Nodes: nodesByChart[chart.Id].OrderBy(x => x.Start).ThenBy(x => x.End)
                .ThenBy(x => x.Id, StringComparer.Ordinal).ToArray())).ToArray();
        var charts = chartNodes.Select(chart => new ScenarioRangeFlowChart(chart.Id, chart.Nodes.Select(x => x.Id).ToArray())).ToArray();
        return new(charts, chartNodes.SelectMany(x => x.Nodes).ToArray(),
            edges.OrderBy(x => x.FromId, StringComparer.Ordinal).ThenBy(x => x.ToId, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Forms physical chains only from unambiguous occurrence-to-occurrence traversals.</summary>
    private static IEnumerable<Trace> ReadTraces(ScenarioFlowGraph graph, IReadOnlyDictionary<CaptureEventKey, List<Span>> spans)
    {
        var chartIds = graph.Charts.Select(x => x.Id).ToHashSet();
        var byId = graph.Occurrences.Where(x => x.End.SessionId == x.Start.SessionId && x.End.Sequence == x.Start.Sequence
            && x.End.Offset > x.Start.Offset && chartIds.Contains(x.ChartId)).ToDictionary(x => x.Id);
        var links = new HashSet<(string From, string To)>();
        foreach (var edge in graph.Edges)
        {
            // Block adjacency is not a traversal. A cut and a session/chart boundary always terminate a chain.
            foreach (var visit in edge.Traversals)
                if (byId.TryGetValue(visit.FromOccurrenceId, out var from) && byId.TryGetValue(visit.ToOccurrenceId, out var to)
                    && from.ChartId == to.ChartId && from.BlockId == edge.FromBlockId && to.BlockId == edge.ToBlockId
                    && from.Start.SessionId == visit.SessionId && to.Start.SessionId == visit.SessionId
                    && !to.CutBefore && from.End.CompareTo(to.Start) <= 0)
                    links.Add((from.Id, to.Id));
        }
        var incoming = links.GroupBy(x => x.To).ToDictionary(g => g.Key, g => g.Count());
        var next = links.GroupBy(x => x.From).Where(g => g.Count() == 1 && incoming[g.First().To] == 1)
            .ToDictionary(g => g.Key, g => g.First().To);
        var hasPrevious = next.Values.ToHashSet();
        foreach (var first in byId.Values.Where(x => !hasPrevious.Contains(x.Id)).OrderBy(x => x.Start))
        {
            var chain = new List<ScenarioFlowOccurrence>();
            var slices = new List<Slice>();
            var occurrence = first;
            while (true)
            {
                chain.Add(occurrence);
                spans.TryGetValue(new(occurrence.Start.SessionId, occurrence.Start.Sequence), out var sourceSpans);
                slices.AddRange(SplitBySavedRanges(occurrence, sourceSpans ?? []));
                if (!next.TryGetValue(occurrence.Id, out var id)) break;
                occurrence = byId[id];
            }
            yield return new(first.ChartId, chain, slices);
        }
    }

    /// <summary>Splits only at real observed endpoints or a saved-range intersection inside an occurrence.</summary>
    private static IEnumerable<Slice> SplitBySavedRanges(ScenarioFlowOccurrence occurrence, IReadOnlyList<Span> spans)
    {
        var start = occurrence.Start.Offset;
        var end = occurrence.End.Offset;
        var applicable = spans.Where(s => s.Start < end && s.End > start).ToArray();
        if (applicable.Length == 0)
        {
            yield return new(occurrence, start, end, []);
            yield break;
        }
        var points = applicable.SelectMany(s => new[] { Math.Max(s.Start, start), Math.Min(s.End, end) })
            .Append(start).Append(end).Distinct().Order().ToArray();
        for (var i = 0; i + 1 < points.Length; i++)
        {
            var from = points[i];
            var to = points[i + 1];
            yield return new(occurrence, from, to, applicable.Where(s => s.Start <= from && s.End >= to)
                .Select(s => s.RangeId).Distinct().Order().ToArray());
        }
    }

    /// <summary>Tracks each saved range within consecutive observed slices; ranges may overlap without merging.</summary>
    private static void FindSavedVisits(Trace trace)
    {
        var previous = new Dictionary<long, SavedVisit>();
        for (var index = 0; index < trace.Slices.Count; index++)
        {
            foreach (var id in trace.Slices[index].RangeIds)
            {
                if (!previous.TryGetValue(id, out var visit) || visit.Last != index - 1)
                {
                    trace.Visits.Add(visit = new(trace, id, index));
                    previous[id] = visit;
                }
                visit.Last = index;
            }
        }
    }

    /// <summary>Aliases only complete, uniquely backed intervals on one physical trace in the same chart.</summary>
    private static void AddCompleteRevisits(IReadOnlyList<Trace> traces, IReadOnlyList<SavedVisit> visits,
        IReadOnlyDictionary<long, SavedRange> ranges)
    {
        if (visits.Count == 0) return;
        var anchors = traces.SelectMany(trace => trace.Occurrences.Select((occurrence, index) => (Trace: trace, Index: index, Occurrence: occurrence)))
            .ToLookup(x => (x.Trace.ChartId, x.Occurrence.BlockId));
        foreach (var sameRange in visits.GroupBy(x => x.RangeId))
        {
            // A partitioned or overlapping template does not prove a full revisit.
            if (sameRange.Count() != 1) continue;
            var visit = sameRange.First();
            var range = ranges[visit.RangeId];
            // An endpoint with no observed body cannot be used to assert a complete correspondence.
            if (visit.Start != range.Start || visit.End != range.End) continue;
            var source = visit.Trace.Slices.GetRange(visit.First, visit.Last - visit.First + 1);
            if (source.Any(x => x.RangeIds.Length != 1) ||
                source.Sum(x => (long)x.End.Offset - x.Start.Offset) != range.Length) continue;
            var pattern = source.GroupBy(x => x.Occurrence.Id).Select(group =>
            {
                var occurrence = group.First().Occurrence;
                return new PatternPart(occurrence.BlockId, group.First().Start.Offset - occurrence.Start.Offset,
                    group.Last().End.Offset - occurrence.Start.Offset, occurrence.End.Offset - occurrence.Start.Offset);
            }).ToArray();
            foreach (var anchor in anchors[(visit.Trace.ChartId, pattern[0].BlockId)])
            {
                // Within one session, reusing the node across disconnected traces could conceal an explicit cut.
                if (anchor.Occurrence.Start.SessionId == visit.Start.SessionId && !ReferenceEquals(anchor.Trace, visit.Trace)) continue;
                var replay = MatchPattern(anchor.Trace, anchor.Index, pattern, visit.Node);
                if (replay is not null && !anchor.Trace.Visits.Any(current =>
                    current.Start.CompareTo(replay.End) < 0 && replay.Start.CompareTo(current.End) < 0))
                    anchor.Trace.Repeats.Add(replay);
            }
        }
        foreach (var trace in traces)
        {
            // Overlapping candidates (including different template IDs for the same replay) remain unset.
            var offered = trace.Repeats.OrderBy(x => x.Start).ThenBy(x => x.End).ToArray();
            trace.Repeats.Clear();
            for (var i = 0; i < offered.Length;)
            {
                var first = offered[i++];
                var end = first.End;
                var unique = true;
                while (i < offered.Length && offered[i].Start.CompareTo(end) < 0)
                {
                    unique = false;
                    if (offered[i].End.CompareTo(end) > 0) end = offered[i].End;
                    i++;
                }
                if (unique) trace.Repeats.Add(first);
            }
        }
    }

    /// <summary>Matches confirmed block IDs and their offsets, never names, labels, or literal text.</summary>
    private static Revisit? MatchPattern(Trace trace, int startIndex, IReadOnlyList<PatternPart> pattern, ScenarioRangeFlowNode node)
    {
        if (startIndex + pattern.Count > trace.Occurrences.Count) return null;
        for (var i = 0; i < pattern.Count; i++)
        {
            var occurrence = trace.Occurrences[startIndex + i];
            if (occurrence.BlockId != pattern[i].BlockId || occurrence.End.Offset - occurrence.Start.Offset != pattern[i].BlockLength)
                return null;
        }
        var first = trace.Occurrences[startIndex].Start;
        var last = trace.Occurrences[startIndex + pattern.Count - 1].Start;
        return new(first with { Offset = first.Offset + pattern[0].StartInBlock },
            last with { Offset = last.Offset + pattern[^1].EndInBlock }, node);
    }

    /// <summary>Refines unsaved pieces at the boundaries of accepted complete revisits.</summary>
    private static IEnumerable<Piece> ReadPieces(Trace trace)
    {
        var replayIndex = 0;
        foreach (var slice in trace.Slices)
        {
            if (slice.RangeIds.Length != 0)
            {
                yield return new(slice.Start, slice.End, slice.Node, slice.VisitStart, slice.RangeIds.Length > 1);
                continue;
            }
            var start = slice.Start;
            while (replayIndex < trace.Repeats.Count)
            {
                var replay = trace.Repeats[replayIndex];
                if (replay.End.CompareTo(start) <= 0) { replayIndex++; continue; }
                if (replay.Start.CompareTo(slice.End) >= 0) break;
                if (start.CompareTo(replay.Start) < 0)
                {
                    yield return new(start, replay.Start, null);
                    start = replay.Start;
                }
                var end = replay.End.CompareTo(slice.End) < 0 ? replay.End : slice.End;
                yield return new(start, end, replay.Node, replay.Start);
                start = end;
                if (start == slice.End) break;
            }
            if (start.CompareTo(slice.End) < 0) yield return new(start, slice.End, null);
        }
    }

    /// <summary>Coalesces consecutive unset pieces. Ambiguous overlaps and trace boundaries cannot invent a transition.</summary>
    private static void ConnectTrace(Trace trace, List<ScenarioRangeFlowNode> nodes, HashSet<ScenarioRangeFlowEdge> edges)
    {
        ScenarioRangeFlowNode? previous = null;
        CapturePosition? previousVisit = null;
        CapturePosition? start = null;
        CapturePosition? end = null;
        void Connect(ScenarioRangeFlowNode current, CapturePosition? visit)
        {
            // A repeated complete visit can loop to the same node; multiple slices of one visit cannot.
            if (previous is not null && (previous.Id != current.Id || previousVisit != visit))
                edges.Add(new(previous.Id, current.Id));
            previous = current;
            previousVisit = visit;
        }
        void FlushUnset()
        {
            if (start is null || end is null) return;
            var node = new ScenarioRangeFlowNode($"{trace.ChartId}/interval:{PositionId(start)}", UnsetRangeName, null, start, end);
            nodes.Add(node);
            Connect(node, start);
            start = end = null;
        }
        foreach (var piece in ReadPieces(trace))
        {
            if (!piece.Ambiguous && piece.Node is null)
            {
                start ??= piece.Start;
                end = piece.End;
                continue;
            }
            FlushUnset();
            if (piece.Ambiguous) previous = null;
            else if (piece.Node is { } node) Connect(node, piece.VisitStart);
        }
        FlushUnset();
    }

    /// <summary>Encodes the stable raw position independently of display culture.</summary>
    private static string PositionId(CapturePosition position) =>
        FormattableString.Invariant($"{position.SessionId}:{position.Sequence}:{position.Offset}");
}
