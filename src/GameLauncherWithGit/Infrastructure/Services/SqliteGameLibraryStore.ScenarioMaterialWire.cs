using GameLauncherWithGit.Application.Models;
using System.Globalization;

namespace GameLauncherWithGit.Infrastructure.Services;

/// <summary>Projects the domain snapshot into the stable JSON wire shape used by scenario archives.</summary>
internal static class ScenarioMaterialWireAdapter
{
    public static ScenarioMaterialWireSnapshot ToWire(ScenarioMaterialSnapshot snapshot) => new(
        ToWire(snapshot.Version),
        snapshot.MatchingComplete,
        snapshot.Contents.Select(content => new ScenarioMaterialWireContent(content.Id, content.Text, KindName(content.Kind))).ToArray(),
        snapshot.Occurrences.Select(occurrence => new ScenarioMaterialWireOccurrence(
            occurrence.Id,
            ToWire(occurrence.Source),
            occurrence.ContentId,
            occurrence.Speaker,
            Identifier(occurrence.LabelId),
            occurrence.LabelName,
            occurrence.ReceivedAt,
            occurrence.Images.Select(image => new ScenarioMaterialWireImage(image.ImageId, image.CapturedAt)).ToArray())).ToArray(),
        snapshot.Matches.Select(match => new ScenarioMaterialWireMatch(match.Id,
            match.Items.Select(pair => new ScenarioMaterialWirePair(ToWire(pair.Representative), ToWire(pair.Revisit))).ToArray())).ToArray());

    public static ScenarioMaterialWireVersion ToWire(ScenarioMaterialVersion version) => new(
        Identifier(version.CaptureGameId), version.Revision, version.MatchingRevision);

    private static ScenarioMaterialWireReference ToWire(ScenarioOccurrenceRef reference) => new(
        Identifier(reference.CaptureGameId), Identifier(reference.SessionId), reference.ReceiveSequence,
        reference.StartUtf16, reference.LengthUtf16);

    private static string Identifier(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string? Identifier(long? value) => value.HasValue ? Identifier(value.Value) : null;

    public static string? KindName(CaptureKind? kind) => kind switch
    {
        CaptureKind.Dialogue => nameof(CaptureKind.Dialogue),
        CaptureKind.Narration => nameof(CaptureKind.Narration),
        CaptureKind.Other => nameof(CaptureKind.Other),
        null => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown capture kind cannot be written to a scenario archive.")
    };
}

internal sealed record ScenarioMaterialWireSnapshot(
    ScenarioMaterialWireVersion Version,
    bool MatchingComplete,
    IReadOnlyList<ScenarioMaterialWireContent> Contents,
    IReadOnlyList<ScenarioMaterialWireOccurrence> Occurrences,
    IReadOnlyList<ScenarioMaterialWireMatch> Matches);

internal sealed record ScenarioMaterialWireVersion(string CaptureGameId, long Revision, long MatchingRevision);
internal sealed record ScenarioMaterialWireContent(string Id, string Text, string? Kind);
internal sealed record ScenarioMaterialWireOccurrence(string Id, ScenarioMaterialWireReference Source, string ContentId,
    string? Speaker, string? LabelId, string? LabelName, DateTimeOffset ReceivedAt,
    IReadOnlyList<ScenarioMaterialWireImage> Images);
internal sealed record ScenarioMaterialWireReference(string CaptureGameId, string SessionId, long ReceiveSequence,
    int StartUtf16, int LengthUtf16);
internal sealed record ScenarioMaterialWireImage(string ImageId, DateTimeOffset CapturedAt);
internal sealed record ScenarioMaterialWireMatch(string Id, IReadOnlyList<ScenarioMaterialWirePair> Items);
internal sealed record ScenarioMaterialWirePair(ScenarioMaterialWireReference Representative, ScenarioMaterialWireReference Revisit);
