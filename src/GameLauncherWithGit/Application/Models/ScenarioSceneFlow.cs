namespace GameLauncherWithGit.Application.Models;

public sealed record ScenarioGameTag(long Id, string Name);
public sealed record ScenarioSceneEdit(string Title, string? DisplayNumber, string? Chapter, double X, double Y,
    IReadOnlyList<long> RangeIds, IReadOnlyList<long> TagIds);
public sealed record ScenarioScene(long Id, ScenarioSceneEdit Definition);
/// <summary>Explicitly authored connections, not observed capture transitions.</summary>
public sealed record ScenarioSceneConnection(long FromSceneId, long ToSceneId);
public sealed record ScenarioRangeTagLink(long RangeId, long TagId);
public sealed record ScenarioSceneFlowSnapshot(long CaptureGameId, ScenarioMaterialVersion Version,
    IReadOnlyList<ScenarioGameTag> Tags, IReadOnlyList<ScenarioRangeTagLink> RangeTags,
    IReadOnlyList<ScenarioScene> Scenes, IReadOnlyList<ScenarioSceneConnection> Connections);
