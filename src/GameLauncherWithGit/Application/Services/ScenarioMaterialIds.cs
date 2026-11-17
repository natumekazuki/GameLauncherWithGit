using System.Globalization;
using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Services;

public static class ScenarioMaterialIds
{
    public static string Occurrence(ScenarioOccurrenceRef source) => string.Create(CultureInfo.InvariantCulture,
        $"occ:{source.CaptureGameId}:{source.SessionId}:{source.ReceiveSequence}:{source.StartUtf16}:{source.LengthUtf16}");
    public static string Content(ScenarioOccurrenceRef selectedRepresentative) => "content:" + Occurrence(selectedRepresentative);
    public static string Node(long gameId, string blockId) => string.Create(CultureInfo.InvariantCulture, $"node:{gameId}:{blockId.Replace('/', '~')}");
    public static string Chart(long gameId, string chartId) => string.Create(CultureInfo.InvariantCulture, $"flow:{gameId}:{chartId}");

    public static ScenarioMaterialSelectionVersion SelectionVersion(ScenarioMaterialSnapshot snapshot) => new(
        snapshot.Occurrences.Select(x => x.Id).ToArray(), snapshot.Ranges.Select(x => x.Id).ToArray(),
        snapshot.SceneFlow.Scenes.OrderBy(x => x.Id).Select(x => x.Id.ToString(CultureInfo.InvariantCulture)).ToArray(),
        snapshot.SceneFlow.Tags.OrderBy(x => x.Id).Select(x => x.Id.ToString(CultureInfo.InvariantCulture)).ToArray());
}
