using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Components;

/// <summary>Identifies a raw text position within one recording root, independent of item splitting.</summary>
public readonly record struct CaptureOccurrenceKey(long RecordId, CapturePosition Position)
{
    /// <summary>Identifies the source event to which images are attached.</summary>
    public CaptureEventKey EventKey => new(Position.SessionId, Position.Sequence);
}
