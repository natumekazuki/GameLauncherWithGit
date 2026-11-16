using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Services;

public static class ScenarioFlowBuilder
{
    public static ScenarioFlowGraph Build(IReadOnlyList<ScenarioFlowPart> parts, ScenarioFlowOverrides overrides)
    {
        var occurrences = new List<ScenarioFlowOccurrence>();
        var edges = new Dictionary<(string From, string To), List<ScenarioFlowTraversal>>();
        var scopes = new Dictionary<string, string>();
        // A cut in one visit splits the shared block at the same offset in every visit.
        // Only the targeted occurrence loses its incoming traversal.
        var splitOffsets = parts.GroupBy(x => x.BlockId).ToDictionary(g => g.Key, g => g.SelectMany(part =>
            overrides.Cuts.Concat(overrides.Regions.SelectMany(x => new[] { x.Start, x.End }))
                .Where(p => p.SessionId == part.Start.SessionId && p.Sequence == part.Start.Sequence
                    && p.Offset > part.Start.Offset && p.Offset < part.End.Offset)
                .Select(p => p.Offset - part.Start.Offset)).Distinct().ToArray());
        ScenarioFlowOccurrence? previous = null;
        foreach (var part in parts.OrderBy(x => x.Start))
        {
            if (part.Start.SessionId != part.End.SessionId || part.Start.Sequence != part.End.Sequence
                || part.End.Offset - part.Start.Offset != part.Text.Length || part.Text.Length == 0)
                throw new ArgumentException("本文の原文範囲が不正です。", nameof(parts));
            var boundaries = splitOffsets[part.BlockId].Where(offset => offset > 0 && offset < part.Text.Length)
                .Select(offset => part.Start.Offset + offset).Append(part.Start.Offset).Append(part.End.Offset)
                .Distinct().Order().ToArray();
            for (var index = 0; index < boundaries.Length - 1; index++)
            {
                var start = part.Start with { Offset = boundaries[index] };
                var end = part.End with { Offset = boundaries[index + 1] };
                var region = overrides.Regions.FirstOrDefault(x => x.Start.CompareTo(start) <= 0 && x.End.CompareTo(end) >= 0);
                var scope = region is null ? "main" : $"independent:{region.Id}";
                var id = PositionId(start);
                var blockId = $"{scope}/{part.BlockId}/{start.Offset - part.Start.Offset}:{end.Offset - start.Offset}";
                var cut = overrides.Cuts.Contains(start);
                var occurrence = new ScenarioFlowOccurrence(id, blockId, "", start, end,
                    part.Text.Substring(start.Offset - part.Start.Offset, end.Offset - start.Offset), part.Speaker, cut);
                scopes[id] = scope;
                occurrences.Add(occurrence);
                // Do not skip removed/isolated regions and synthesize a new edge across them.
                var crossesRegion = previous is not null && overrides.Regions.Any(r =>
                    Crosses(previous.End, start, r.Start) || Crosses(previous.End, start, r.End));
                if (previous is not null && previous.Start.SessionId == start.SessionId && !cut
                    && scopes[previous.Id] == scope && !crossesRegion)
                {
                    var key = (previous.BlockId, blockId);
                    if (!edges.TryGetValue(key, out var traversals)) edges[key] = traversals = [];
                    traversals.Add(new(previous.Id, id, start.SessionId));
                }
                previous = occurrence;
            }
        }
        var blocks = occurrences.GroupBy(x => x.BlockId).Select(g => new ScenarioFlowBlock(g.Key,
            g.First().Text, g.Select(x => x.Id).ToArray())).ToArray();
        var parents = blocks.ToDictionary(x => x.Id, x => x.Id);
        string Root(string id)
        {
            var root = id;
            while (parents[root] != root) root = parents[root];
            while (parents[id] != id) { var next = parents[id]; parents[id] = root; id = next; }
            return root;
        }
        foreach (var edge in edges.Keys) parents[Root(edge.To)] = Root(edge.From);
        var chartIds = occurrences.GroupBy(x => Root(x.BlockId)).ToDictionary(g => g.Key,
            g => $"chart:{g.First().Id}");
        var assigned = occurrences.Select(x => x with { ChartId = chartIds[Root(x.BlockId)] }).ToArray();
        return new(assigned.GroupBy(x => x.ChartId).Select(g => new ScenarioFlowChart(g.Key, g.Select(x => x.Id).ToArray())).ToArray(),
            blocks, assigned, edges.Select(x => new ScenarioFlowEdge(x.Key.From, x.Key.To, x.Value)).ToArray());
    }

    private static bool Crosses(CapturePosition previousEnd, CapturePosition start, CapturePosition boundary) =>
        previousEnd.CompareTo(boundary) <= 0 && start.CompareTo(boundary) >= 0 && previousEnd.CompareTo(start) < 0;

    public static string PositionId(CapturePosition position) => $"{position.SessionId}:{position.Sequence}:{position.Offset}";
}
