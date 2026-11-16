using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Services;

public static class ScenarioMaterialImageAccess
{
    /// <summary>Call under the image gate with the same transaction snapshot used to resolve the request selection.</summary>
    public static CaptureEventKey Validate(ScenarioMaterialSnapshot snapshot, ScenarioMaterialAccessLevel level, ScenarioMaterialImageRequest request)
    {
        if (level != ScenarioMaterialAccessLevel.Full) throw new UnauthorizedAccessException("画像本体の読取権限がありません。");
        var version = snapshot.Version;
        if (request.CaptureGameId != version.CaptureGameId
            || request.Version.CaptureGameId != version.CaptureGameId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || request.Version.Revision != version.Revision || request.Version.MatchingRevision != version.MatchingRevision)
            throw new InvalidOperationException("資料版が更新されています。");
        var selection = new ScenarioMaterialSelectionVersion(snapshot.Occurrences.Select(x => x.Id).ToArray(), snapshot.Ranges.Select(x => x.Id).ToArray());
        if (!selection.SameSelection(request.SelectionVersion)) throw new InvalidOperationException("選択範囲が更新されています。");
        var occurrence = snapshot.Occurrences.FirstOrDefault(x => x.Id == request.OccurrenceId);
        if (occurrence is null)
        {
            var flow = snapshot.Graph.Occurrences.FirstOrDefault(x => ScenarioMaterialIds.Occurrence(new(snapshot.Version.CaptureGameId,
                x.Start.SessionId, x.Start.Sequence, x.Start.Offset, x.End.Offset - x.Start.Offset)) == request.OccurrenceId);
            if (flow is not null) occurrence = snapshot.Occurrences.FirstOrDefault(x => x.Source.SessionId == flow.Start.SessionId
                && x.Source.ReceiveSequence == flow.Start.Sequence && x.Source.StartUtf16 <= flow.Start.Offset
                && x.Source.StartUtf16 + x.Source.LengthUtf16 >= flow.End.Offset);
        }
        if (occurrence is null || !occurrence.Images.Any(x => x.ImageId == request.ImageId))
            throw new UnauthorizedAccessException("選択した出現の添付画像ではありません。");
        return new(occurrence.Source.SessionId, occurrence.Source.ReceiveSequence);
    }
}
