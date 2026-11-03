using GameLauncherWithGit.Application.Models;
using System.Text;
using System.Globalization;

namespace GameLauncherWithGit.Application.Services;

public static class ScenarioTextProcessor
{
    public static IReadOnlyList<CaptureSegment> Split(string text, CaptureRules rules)
    {
        if (text.Length == 0) return [];
        if (!rules.SplitDialogue) return [new(0, text.Length, null)];
        var result = new List<CaptureSegment>();
        var cursor = 0;
        var start = -1;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '「')
            {
                if (depth++ == 0) start = i;
            }
            else if (text[i] == '」' && depth > 0 && --depth == 0)
            {
                if (start > cursor) result.Add(new(cursor, start - cursor, rules.OutsideIsNarration ? CaptureKind.Narration : null));
                result.Add(new(start, i - start + 1, CaptureKind.Dialogue));
                cursor = i + 1;
            }
        }
        if (cursor < text.Length)
            result.Add(new(cursor, text.Length - cursor, depth == 0 && rules.OutsideIsNarration ? CaptureKind.Narration : null));
        return result;
    }

    public static void ValidateBoundaries(string raw, IReadOnlyList<int> boundaries)
    {
        var previous = 0;
        var textElements = StringInfo.ParseCombiningCharacters(raw).ToHashSet();
        foreach (var position in boundaries)
        {
            if (position <= previous || position >= raw.Length
                || !textElements.Contains(position))
                throw new ArgumentException("分割位置が不正です。文字の途中は指定できません。");
            previous = position;
        }
    }

    public static Dictionary<long, string?> MatchSpeakers(IReadOnlyList<CaptureReceived> events,
        IReadOnlyList<CaptureTextItem> items, IReadOnlyCollection<string> names, DateTimeOffset now)
    {
        var result = new Dictionary<long, string?>();
        var dialogue = items.Where(x => x.Kind == CaptureKind.Dialogue || x.Kind is null).ToArray();
        var nameEvents = events.Where(x => x.Role == CaptureRole.Name).ToArray();
        foreach (var item in dialogue)
        {
            if (item.ReceivedAt.AddSeconds(2) > now) continue;
            result[item.Id] = null;
            if (dialogue.Count(x => x.Sequence == item.Sequence) != 1) continue;
            var near = nameEvents.Where(x => Math.Abs((x.ReceivedAt - item.ReceivedAt).TotalSeconds) <= 1).ToArray();
            if (near.Length != 1) continue;
            var nameEvent = near[0];
            if (nameEvent.ReceivedAt.AddSeconds(1) > now
                || dialogue.Count(x => Math.Abs((x.ReceivedAt - nameEvent.ReceivedAt).TotalSeconds) <= 1) != 1) continue;
            var name = nameEvent.Text.Trim();
            if (names.Contains(name, StringComparer.Ordinal)) result[item.Id] = name;
        }
        return result;
    }

    public static string FormatTxt(IEnumerable<CaptureTextItem> items)
    {
        var output = new StringBuilder();
        foreach (var item in items)
        {
            if (output.Length > 0) output.AppendLine();
            var label = item.LabelName ?? "未判定";
            if (item.Kind == CaptureKind.Dialogue || (item.Kind is null && item.Speaker is not null))
                output.Append('[').Append(label).Append('：').Append(item.Speaker ?? "話者未確定").AppendLine("]");
            else output.Append('[').Append(label).AppendLine("]");
            output.AppendLine(item.Text);
        }
        return output.ToString();
    }
}
