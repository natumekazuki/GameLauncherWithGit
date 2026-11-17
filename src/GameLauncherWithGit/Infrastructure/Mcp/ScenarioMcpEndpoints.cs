using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GameLauncherWithGit.Infrastructure.Mcp;

/// <summary>Every entry point resolves the current HTTP principal before reading a tier-specific projection.</summary>
[McpServerToolType, McpServerResourceType]
public sealed class ScenarioMcpEndpoints(IHttpContextAccessor context, IScenarioCaptureStore captures,
    IScenarioMaterialReader materials, IScenarioScreenshotStore screenshots)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private ScenarioMaterialAccessLevel Level => context.HttpContext?.Items[ScenarioMcpHost.AccessLevelKey]
        is ScenarioMaterialAccessLevel level ? level : throw new McpException("Authorization required.");

    [McpServerTool(Name = "scenario_games", ReadOnly = true, OpenWorld = false)]
    [Description("List capture game IDs. Use these IDs with scenario_snapshot; does not return titles or file paths.")]
    public Task<string> Games() => Safe(async () =>
    {
        _ = Level;
        return JsonSerializer.Serialize((await captures.GetCaptureProfilesAsync()).Select(x => new
        { captureGameId = x.Id.ToString(CultureInfo.InvariantCulture) }), Json);
    });

    [McpServerTool(Name = "scenario_snapshot", ReadOnly = true, OpenWorld = false)]
    [Description("Read the selected scenario material at the API key's level. Returns stable IDs, material version and selection version. Full keys also receive text and speakers.")]
    public Task<string> Snapshot(string gameId, ScenarioMcpSelection? selection = null,
        ScenarioMaterialWireVersion? expectedVersion = null, CancellationToken cancellationToken = default) =>
        Safe(async () => Serialize(await Read(gameId, selection, expectedVersion, cancellationToken)));

    [McpServerTool(Name = "scenario_export_json", ReadOnly = true, OpenWorld = false)]
    [Description("Export selected material as JSON using the same IDs, versions and key permissions as scenario_snapshot. Does not write a file.")]
    public Task<string> ExportJson(string gameId, ScenarioMcpSelection? selection = null,
        ScenarioMaterialWireVersion? expectedVersion = null, CancellationToken cancellationToken = default) =>
        Snapshot(gameId, selection, expectedVersion, cancellationToken);

    [McpServerTool(Name = "scenario_by_id", ReadOnly = true, OpenWorld = false)]
    [Description("Read an ID from contents, occurrences, matches, ranges, classifications, images, charts, blocks, flowOccurrences, scenes or tags. Requires the version from scenario_snapshot; IDs never bypass key permissions or selection.")]
    public Task<string> ById(string gameId, string collection, string id, ScenarioMaterialWireVersion expectedVersion,
        ScenarioMcpSelection? selection = null, CancellationToken cancellationToken = default) => Safe(async () =>
    {
        ArgumentNullException.ThrowIfNull(expectedVersion);
        var snapshot = await Read(gameId, selection, expectedVersion, cancellationToken);
        var json = JsonSerializer.SerializeToElement(snapshot, snapshot.GetType(), Json);
        var array = collection switch
        {
            "contents" or "occurrences" or "matches" or "ranges" or "classifications" or "images" => json.GetProperty(collection),
            "charts" or "blocks" => json.GetProperty("graph").GetProperty(collection),
            "flowOccurrences" => json.GetProperty("graph").GetProperty("occurrences"),
            "scenes" or "tags" => json.GetProperty("sceneFlow").GetProperty(collection),
            _ => throw new McpException("Unknown collection.")
        };
        var item = array.EnumerateArray().FirstOrDefault(x => x.GetProperty("id").GetString() == id);
        if (item.ValueKind == JsonValueKind.Undefined) throw new McpException("Selected item not found.");
        return JsonSerializer.Serialize(new { snapshot.Version, snapshot.SelectionVersion, item }, Json);
    });

    [McpServerTool(Name = "scenario_by_source", ReadOnly = true, OpenWorld = false)]
    [Description("Resolve an exact original occurrence reference within the selection and expected version. Returns only fields permitted by the current API key.")]
    public Task<string> BySource(ScenarioMaterialWireReference source, ScenarioMaterialWireVersion expectedVersion,
        ScenarioMcpSelection? selection = null, CancellationToken cancellationToken = default) => Safe(async () =>
    {
        ArgumentNullException.ThrowIfNull(expectedVersion);
        var snapshot = await Read(source.CaptureGameId, selection, expectedVersion, cancellationToken);
        var json = JsonSerializer.SerializeToElement(snapshot, snapshot.GetType(), Json);
        var item = json.GetProperty("occurrences").EnumerateArray()
            .Concat(json.GetProperty("graph").GetProperty("occurrences").EnumerateArray())
            .FirstOrDefault(x => MatchesSource(x.GetProperty("source").Deserialize<ScenarioMaterialWireReference>(Json)!, source));
        if (item.ValueKind == JsonValueKind.Undefined) throw new McpException("Selected item not found.");
        return JsonSerializer.Serialize(new { snapshot.Version, snapshot.SelectionVersion, item }, Json);
    });

    [McpServerResource(UriTemplate = "scenario://material/{gameId}/{revision}/{matchingRevision}", Name = "scenario_material", MimeType = "application/json")]
    [Description("Read all material at an exact version using the current key's permissions. Use scenario_snapshot for filtered selections.")]
    public Task<string> MaterialResource(string gameId, long revision, long matchingRevision, CancellationToken cancellationToken) =>
        Snapshot(gameId, null, new(gameId, revision, matchingRevision), cancellationToken);

    [McpServerResource(UriTemplate = "scenario://item/{gameId}/{revision}/{matchingRevision}/{collection}/{id}", Name = "scenario_item", MimeType = "application/json")]
    [Description("Read an exact-version item by ID using the current key's permissions.")]
    public Task<string> ItemResource(string gameId, long revision, long matchingRevision, string collection, string id,
        CancellationToken cancellationToken) => ById(gameId, collection, id, new(gameId, revision, matchingRevision), null, cancellationToken);

    [McpServerTool(Name = "scenario_image", ReadOnly = true, OpenWorld = false)]
    [Description("Read a PNG attached to the exact selected occurrence. Full key required. Supply the material and selection versions from scenario_snapshot, not a file path.")]
    public Task<ImageContentBlock> Image(ScenarioMcpImageRequest request, CancellationToken cancellationToken = default) =>
        Safe(async () => ImageContentBlock.FromBytes(await ReadImage(request, cancellationToken), "image/png"));

    [McpServerTool(Name = "scenario_image_by_source", ReadOnly = true, OpenWorld = false)]
    [Description("Read an occurrence's PNG by exact original source reference and expected material/selection version. Full key required.")]
    public Task<ImageContentBlock> ImageBySource(ScenarioMaterialWireReference source, string imageId,
        ScenarioMaterialWireVersion expectedVersion, ScenarioMaterialSelectionVersion selectionVersion,
        ScenarioMcpSelection? selection = null, CancellationToken cancellationToken = default) => Safe(async () =>
    {
        RequireFull();
        ArgumentNullException.ThrowIfNull(expectedVersion);
        var snapshot = (ScenarioFullProjection)await Read(source.CaptureGameId, selection, expectedVersion, cancellationToken);
        var occurrenceId = snapshot.Occurrences.FirstOrDefault(x => MatchesSource(x.Source, source))?.Id
            ?? snapshot.Graph.Occurrences.FirstOrDefault(x => MatchesSource(x.Source, source))?.Id
            ?? throw new McpException("Selected item not found.");
        return ImageContentBlock.FromBytes(await ReadImage(new(source.CaptureGameId, selection ?? new(), snapshot.Version,
            selectionVersion, occurrenceId, imageId), cancellationToken), "image/png");
    });

    [McpServerResource(UriTemplate = "scenario://image/{gameId}/{revision}/{matchingRevision}/{occurrenceId}/{imageId}", Name = "scenario_image", MimeType = "image/png")]
    [Description("Read an exact-version occurrence PNG from the all-material selection. Full key required; no file paths are accepted.")]
    public Task<BlobResourceContents> ImageResource(string gameId, long revision, long matchingRevision,
        string occurrenceId, string imageId, CancellationToken cancellationToken) => Safe(async () =>
    {
        RequireFull();
        var snapshot = await Read(gameId, null, new(gameId, revision, matchingRevision), cancellationToken);
        var bytes = await ReadImage(new(gameId, new(), snapshot.Version, snapshot.SelectionVersion, occurrenceId, imageId), cancellationToken);
        var uri = $"scenario://image/{gameId}/{revision}/{matchingRevision}/{Uri.EscapeDataString(occurrenceId)}/{Uri.EscapeDataString(imageId)}";
        return BlobResourceContents.FromBytes(bytes, uri, "image/png");
    });

    private void RequireFull()
    {
        if (Level != ScenarioMaterialAccessLevel.Full) throw new McpException("Full access is required for image content.");
    }

    private static bool MatchesSource(ScenarioMaterialWireReference raw, ScenarioMaterialWireReference source) => raw == source;

    private Task<byte[]> ReadImage(ScenarioMcpImageRequest request, CancellationToken cancellationToken)
    {
        RequireFull();
        return screenshots.ReadScenarioImageAsync(Level, request.ToDomain(), cancellationToken);
    }

    private async Task<ScenarioMaterialProjection> Read(string gameId, ScenarioMcpSelection? selection,
        ScenarioMaterialWireVersion? expectedVersion, CancellationToken cancellationToken)
    {
        var level = Level;
        selection ??= new();
        if (level == ScenarioMaterialAccessLevel.Structure &&
            (!string.IsNullOrEmpty(selection.TextSearch) || !string.IsNullOrEmpty(selection.SpeakerSearch)))
            throw new McpException("Selection is not permitted.");
        var id = ScenarioMcpInputs.Id(gameId);
        var snapshot = await materials.GetScenarioProjectionAsync(id, selection.ToDomain(), level, cancellationToken);
        if (expectedVersion is not null && (expectedVersion.CaptureGameId != gameId
            || snapshot.Version.CaptureGameId != gameId
            || snapshot.Version.Revision != expectedVersion.Revision
            || snapshot.Version.MatchingRevision != expectedVersion.MatchingRevision))
            throw new McpException("Material version changed. Read a new snapshot.");
        return snapshot;
    }

    private static string Serialize(ScenarioMaterialProjection snapshot) => JsonSerializer.Serialize(snapshot, snapshot.GetType(), Json);
    private static async Task<T> Safe<T>(Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (McpException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch { throw new McpException("Scenario material is unavailable or the request is invalid."); }
    }
}
