using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Services;

/// <summary>Only confirmed match spans share blocks; equal short text alone is not evidence.</summary>
public static class ScenarioFlowMaterialAdapter
{
    private static (long Session, long Sequence) Event(ScenarioOccurrenceRef source) => (source.SessionId, source.ReceiveSequence);
    public static ScenarioFlowGraph Build(ScenarioMaterialSnapshot material, ScenarioFlowOverrides overrides)
    {
        var contents = material.Contents.ToDictionary(x => x.Id);
        var boundaries = new Dictionary<(long, long), SortedSet<int>>();
        void Add(ScenarioOccurrenceRef source)
        {
            if (!boundaries.TryGetValue(Event(source), out var offsets)) boundaries[Event(source)] = offsets = [];
            offsets.Add(source.StartUtf16); offsets.Add(source.StartUtf16 + source.LengthUtf16);
        }
        foreach (var occurrence in material.Occurrences) Add(occurrence.Source);
        var pairs = material.Matches.SelectMany(x => x.Items).ToArray();
        foreach (var pair in pairs) { Add(pair.Representative); Add(pair.Revisit); }
        foreach (var position in overrides.Cuts.Concat(overrides.Regions.SelectMany(x => new[] { x.Start, x.End })))
            if (boundaries.TryGetValue((position.SessionId, position.Sequence), out var offsets)) offsets.Add(position.Offset);
        // Transfer every split point through the confirmed correspondence, including later visits.
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in pairs)
            {
                Transfer(pair.Representative, pair.Revisit);
                Transfer(pair.Revisit, pair.Representative);
            }
        } while (changed);
        void Transfer(ScenarioOccurrenceRef from, ScenarioOccurrenceRef to)
        {
            if (from.LengthUtf16 != to.LengthUtf16) throw new ArgumentException("照合範囲の長さが一致しません。");
            foreach (var offset in boundaries[Event(from)].Where(x => x >= from.StartUtf16 && x <= from.StartUtf16 + from.LengthUtf16).ToArray())
                changed |= boundaries[Event(to)].Add(to.StartUtf16 + offset - from.StartUtf16);
        }
        var parts = new List<ScenarioFlowPart>();
        foreach (var occurrence in material.Occurrences)
        {
            var source = occurrence.Source;
            var text = contents[occurrence.ContentId].Text;
            if (text.Length != source.LengthUtf16) throw new ArgumentException("本文と出現範囲の長さが一致しません。");
            var points = boundaries[Event(source)].Where(x => x >= source.StartUtf16 && x <= source.StartUtf16 + source.LengthUtf16).ToArray();
            for (var index = 0; index < points.Length - 1; index++)
            {
                var start = new CapturePosition(source.SessionId, source.ReceiveSequence, points[index]);
                parts.Add(new(ScenarioFlowBuilder.PositionId(start), start, start with { Offset = points[index + 1] },
                    text.Substring(points[index] - source.StartUtf16, points[index + 1] - points[index]), occurrence.Speaker));
            }
        }
        var parents = parts.ToDictionary(x => x.BlockId, x => x.BlockId);
        string Root(string id)
        {
            var root = id;
            while (parents[root] != root) root = parents[root];
            while (parents[id] != id) { var next = parents[id]; parents[id] = root; id = next; }
            return root;
        }
        var byPosition = parts.ToDictionary(x => x.Start);
        foreach (var pair in pairs)
        {
            foreach (var part in parts.Where(x => x.Start.SessionId == pair.Representative.SessionId
                && x.Start.Sequence == pair.Representative.ReceiveSequence && x.Start.Offset >= pair.Representative.StartUtf16
                && x.End.Offset <= pair.Representative.StartUtf16 + pair.Representative.LengthUtf16))
            {
                var target = new CapturePosition(pair.Revisit.SessionId, pair.Revisit.ReceiveSequence,
                    pair.Revisit.StartUtf16 + part.Start.Offset - pair.Representative.StartUtf16);
                if (!byPosition.TryGetValue(target, out var revisit) || part.Text != revisit.Text) continue;
                var a = Root(part.BlockId); var b = Root(revisit.BlockId);
                if (StringComparer.Ordinal.Compare(a, b) < 0) parents[b] = a; else parents[a] = b;
            }
        }
        return ScenarioFlowBuilder.Build(parts.Select(x => x with { BlockId = Root(x.BlockId) }).ToArray(), overrides);
    }
}
