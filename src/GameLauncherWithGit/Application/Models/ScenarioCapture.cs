namespace GameLauncherWithGit.Application.Models;

public enum CaptureRole { None, Body, Name }
public enum CaptureKind { Dialogue, Narration, Other }

public sealed record CaptureSelection(string HookCode, string HookName, ulong Context, ulong Context2,
    CaptureRole Role, long? LabelId = null)
{
    public bool Matches(CaptureSource source) => HookCode == source.HookCode && HookName == source.HookName
        && (Context & 0xffff) == (source.Context & 0xffff) && (Context2 & 0xffff) == (source.Context2 & 0xffff);
}

public sealed record CaptureRules
{
    public CaptureSelection[] Selections { get; init; } = [];
    public bool SplitDialogue { get; init; } = true;
    public bool OutsideIsNarration { get; init; }
    // Preserve stored user entries when saving settings; speaker matching uses the selected name source.
    public string[] Names { get; init; } = [];
}

public sealed record CaptureProfile(long Id, string? GameId, string Title, bool Enabled,
    string? BodyExecutablePath, CaptureRules Rules);
public sealed record CaptureLabel(long Id, long CaptureGameId, string Name, CaptureKind Kind);
public sealed record CaptureSession(long Id, long CaptureGameId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);
public sealed record CaptureSource(uint ProcessId, ulong Address, ulong Context, ulong Context2, string HookCode, string HookName)
{
    public string Key => $"{ProcessId}:{Address:x}:{Context:x}:{Context2:x}:{HookCode}:{HookName}";
}
public sealed record CaptureReceived(long Sequence, DateTimeOffset ReceivedAt, CaptureSource Source, string Text,
    CaptureRole Role, long? LabelId = null);
public sealed record CaptureSegment(int Start, int Length, CaptureKind? Kind);
public sealed record CaptureManualPart(long? LabelId, string? Speaker);
public sealed record CapturePosition(long SessionId, long Sequence, int Offset) : IComparable<CapturePosition>
{
    public int CompareTo(CapturePosition? other) => other is null ? 1
        : SessionId != other.SessionId ? SessionId.CompareTo(other.SessionId)
        : Sequence != other.Sequence ? Sequence.CompareTo(other.Sequence) : Offset.CompareTo(other.Offset);
}
public sealed record CaptureRange(long Id, long CaptureGameId, string Name, CapturePosition Start, CapturePosition End);
public sealed record CaptureRangeSelection(long CaptureGameId, CapturePosition Start, CapturePosition End);
public sealed record CaptureEventKey(long SessionId, long Sequence);
public sealed record CaptureDeletionTarget(CaptureEventKey Key, string RawText, int ItemCount);
public sealed record CaptureHistoryCounts(int Items, int Events, int Ranges);
public sealed record CaptureTextItem(long Id, long SessionId, long Sequence, DateTimeOffset ReceivedAt,
    string RawText, int Start, int Length, long? LabelId, string? LabelName, CaptureKind? Kind,
    string? Speaker, bool ManualLabel, bool ManualSpeaker, bool ManualBoundaries)
{
    public string Text => RawText.Substring(Start, Length);
    public CapturePosition StartPosition => new(SessionId, Sequence, Start);
    public CapturePosition EndPosition => new(SessionId, Sequence, Start + Length);
}
public sealed record CaptureSample(long Sequence, DateTimeOffset ReceivedAt, string Text, bool Truncated = false);
public sealed record CaptureCandidate(CaptureSource Source, CaptureRole Role, long? LabelId)
{
    public CaptureSample[] Samples { get; init; } = [];
}
public sealed record CaptureProcess(int Id, string Name, string Path, string WindowTitle = "")
{
    public bool Matches(string search) => string.IsNullOrWhiteSpace(search)
        || Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || Path.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || WindowTitle.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || Id.ToString().Contains(search.Trim(), StringComparison.Ordinal);
}

public sealed record CaptureSnapshot
{
    public CaptureProfile? Profile { get; init; }
    public long? SessionId { get; init; }
    public string Status { get; init; } = "未接続";
    public bool NeedsProcess { get; init; }
    public bool Active { get; init; }
    public bool Connected { get; init; }
    public bool Preview { get; init; }
    public bool Connecting { get; init; }
    public bool Recording { get; init; }
    public CaptureProcess? Process { get; init; }
    public long Received { get; init; }
    public long Saved { get; init; }
    public long Lost { get; init; }
    public CaptureCandidate[] Candidates { get; init; } = [];
    public CaptureTextItem[] PreviewItems { get; init; } = [];
    public CapturePosition? RangeStart { get; init; }
}
