using System.Globalization;
using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Mcp;

/// <summary>External integer identifiers are strings so JSON clients never round 64-bit IDs.</summary>
public sealed record ScenarioMcpSelection(string? RangeId = null, string? LabelId = null,
    bool UnclassifiedOnly = false, CaptureKind? Kind = null, string? TextSearch = null, string? SpeakerSearch = null,
    IReadOnlyList<string>? IncludeTags = null, IReadOnlyList<string>? ExcludeTags = null)
{
    internal ScenarioArchiveSelection ToDomain() => new(
        RangeId is null ? null : ScenarioMcpInputs.Id(RangeId),
        LabelId is null ? null : ScenarioMcpInputs.Id(LabelId), UnclassifiedOnly, Kind, TextSearch, SpeakerSearch, IncludeTags, ExcludeTags);
}

public sealed record ScenarioMcpImageRequest(string CaptureGameId, ScenarioMcpSelection Selection,
    ScenarioMaterialWireVersion Version, ScenarioMaterialSelectionVersion SelectionVersion, string OccurrenceId, string ImageId)
{
    internal ScenarioMaterialImageRequest ToDomain() => new(ScenarioMcpInputs.Id(CaptureGameId), Selection.ToDomain(),
        Version, SelectionVersion, OccurrenceId, ImageId);
}

internal static class ScenarioMcpInputs
{
    internal static long Id(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
        ? id : throw new ArgumentException("A positive decimal identifier string is required.");
}
