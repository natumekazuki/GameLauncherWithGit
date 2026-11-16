using System.Text.Json;
using System.Text.Json.Nodes;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioProjectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(), "projection-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteGameLibraryStore _store;
    private static readonly CaptureSource Source = new(10, 100, 200, 0, "H", "body");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Speaker = "機密話者_非公開";
    private static readonly string[] Lines = ["「機密本文_共通の長い冒頭です」", "「機密本文_続く発言も長いです」", "「機密本文_三番目の発言です」",
        "「機密本文_一周目だけの返答」", "「機密本文_再合流する長い本文です」", "「機密本文_合流した次の発言です」", "「機密本文_結びの長い発言です」"];
    public ScenarioProjectionTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
    }
    private async Task<long> GameAsync()
    {
        await _store.UpsertAsync(new("game", "projection", "synthetic.exe", null, null, null, GameCardStatus.Unknown));
        return (await _store.GetOrCreateCaptureProfileAsync("game")).Id;
    }
    private async Task<long> VisitAsync(long game, IReadOnlyList<string> lines)
    {
        var session = await _store.BeginCaptureSessionAsync(game);
        for (var index = 0; index < lines.Count; index++)
            await _store.AppendCaptureAsync(session, new(index + 1, DateTimeOffset.UtcNow, Source, lines[index], CaptureRole.Body), new());
        foreach (var item in await _store.GetCaptureItemsAsync(session, 0)) await _store.SetCaptureSpeakerAsync(session, item.Id, Speaker);
        return session;
    }
    private async Task<(long Game, long First, long Second, string OutsideImage, string SelectedImage)> FixtureAsync()
    {
        var game = await GameAsync();
        var first = await VisitAsync(game, Lines);
        var second = await VisitAsync(game, Lines.Select((text, index) => index == 3 ? "「機密本文_二周目だけの返答」" : text).ToArray());
        await _store.SaveCaptureRangeAsync(game, "outside range", new(first, 1, 0), new(first, 7, Lines[6].Length), ["outside"]);
        await _store.SaveCaptureRangeAsync(game, "selected range", new(second, 1, 0), new(second, 7, Lines[6].Length), ["second"]);
        var outside = await _store.SaveScreenshotAsync(new(game, new(first, 1)), new(2, 2, new string('a', 64), [1]), DateTimeOffset.UtcNow);
        var selected = await _store.SaveScreenshotAsync(new(game, new(second, 4)), new(3, 2, new string('b', 64), [2]), DateTimeOffset.UtcNow);
        return (game, first, second, outside.ImageId, selected.ImageId);
    }
    private static string Serialize(ScenarioMaterialProjection projection) => JsonSerializer.Serialize(projection, Json);
    private static void RemoveContent(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(x => x.Key).Where(x => x is "text" or "speaker").ToArray()) obj.Remove(key);
            foreach (var property in obj) RemoveContent(property.Value);
        }
        else if (node is JsonArray array) foreach (var item in array) RemoveContent(item);
    }
    private static IEnumerable<string> PropertyNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                yield return property.Name;
                foreach (var name in PropertyNames(property.Value)) yield return name;
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (var name in PropertyNames(item)) yield return name;
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "構造/完全の実JSONは同一版とIDを持ち、構造側は全階層で本文・話者のpropertyを省きflow/範囲/分類/画像metadataを保つ"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 連続本文照合と共通資料 (共通projection・raw由来ID・Structureのproperty省略)" }
    // fault = "base型serializationで内部完全snapshotを混ぜる、機密値をnullに置く、または権限でID/版を採番し直す"
    // observable = "両公開projectionのJSON、再帰property名、本文/話者目印の有無、flow/範囲/画像IDと版"
    // observation_boundary = "consumer"
    // scope = "shared two-level material JSON projection"
    // lifecycle = "permanent"
    // impact = "構造権限へ内容を漏らさずMCPとexportが同じ対象を参照できる"
    // distinction = "型/buildだけでは多態serializationの実propertyと識別子を検証できない"
    // @end-test-value
    [Fact]
    public async Task ActualJsonOmitsContentPropertiesAndKeepsTheSameIdentity()
    {
        var fixture = await FixtureAsync();
        var structure = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(fixture.Game, new(), ScenarioMaterialAccessLevel.Structure));
        var full = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(fixture.Game, new(), ScenarioMaterialAccessLevel.Full));
        var structuralJson = Serialize(structure); var fullJson = Serialize(full);
        using var document = JsonDocument.Parse(structuralJson);
        var prohibited = new[] { "text", "rawText", "speaker", "excerpt", "preview", "thumbnail", "bytes", "png", "filePath", "path", "snapshot", "textSearch", "speakerSearch" };
        Assert.DoesNotContain(PropertyNames(document.RootElement), name => prohibited.Contains(name, StringComparer.OrdinalIgnoreCase));
        var decodedStructure = JsonNode.Parse(structuralJson)!.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.DoesNotContain("機密本文", decodedStructure); Assert.DoesNotContain(Speaker, decodedStructure);
        var fullNode = JsonNode.Parse(fullJson);
        Assert.Contains("機密本文", fullNode!.ToJsonString(new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.All(full.Occurrences, x => Assert.Equal(Speaker, x.Speaker));
        RemoveContent(fullNode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(structuralJson), fullNode));
        Assert.Equal(full.Version, structure.Version);
        Assert.True(full.SelectionVersion.SameSelection(structure.SelectionVersion));
        Assert.NotEmpty(structure.Graph.Blocks); Assert.NotEmpty(structure.Graph.Edges);
        Assert.Equal(8, structure.Contents.Count); Assert.Equal(14, structure.Occurrences.Count);
        Assert.Equal(2, structure.Ranges.Count); Assert.Single(structure.Classifications); Assert.Equal(2, structure.Images.Count);
        var repeated = await _store.GetScenarioProjectionAsync(fixture.Game, new(), ScenarioMaterialAccessLevel.Structure);
        Assert.Equal(structuralJson, Serialize(repeated));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "タグ選択は代表が選択外でも両権限へ同じ選択出現/範囲/画像だけを出し本文検索の語は構造JSONに含めない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 連続本文照合と共通資料 (SelectionVersionの値列比較・選択内限定・検索語の省略)" }
    // fault = "共通代表の選択外ID/範囲/画像が混ざるか選択結果scopeを配列参照で比較する"
    // observable = "選択内の公開raw参照・画像/範囲ID、実構造JSON、選択版の値列比較"
    // observation_boundary = "consumer"
    // scope = "selected material metadata and scope identity"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task SelectionExcludesHistoricalReferencesAndUsesValueScopeEquality()
    {
        var fixture = await FixtureAsync();
        var selection = new ScenarioArchiveSelection(IncludeTags: ["second"]);
        var structure = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(fixture.Game, selection, ScenarioMaterialAccessLevel.Structure));
        var full = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(fixture.Game, selection, ScenarioMaterialAccessLevel.Full));
        Assert.True(full.SelectionVersion.SameSelection(structure.SelectionVersion));
        var copiedScope = JsonSerializer.Deserialize<ScenarioMaterialSelectionVersion>(JsonSerializer.Serialize(full.SelectionVersion, Json), Json)!;
        Assert.True(full.SelectionVersion.SameSelection(copiedScope));
        Assert.All(structure.Occurrences, x => Assert.Equal(fixture.Second.ToString(), x.Source.SessionId));
        Assert.All(structure.Graph.Occurrences, x => Assert.Equal(fixture.Second.ToString(), x.Source.SessionId));
        Assert.Empty(structure.Matches);
        Assert.Equal("selected range", Assert.Single(structure.Ranges).Name);
        Assert.Equal(fixture.SelectedImage, Assert.Single(structure.Images).Id);
        Assert.DoesNotContain(structure.Images, x => x.Id == fixture.OutsideImage);
        Assert.DoesNotContain(full.Contents, x => x.Text == Lines[3]);
        var whole = await _store.GetScenarioProjectionAsync(fixture.Game, new(), ScenarioMaterialAccessLevel.Structure);
        Assert.False(whole.SelectionVersion.SameSelection(structure.SelectionVersion));
        var localSearch = await _store.GetScenarioProjectionAsync(fixture.Game,
            selection with { TextSearch = "二周目だけ", SpeakerSearch = Speaker }, ScenarioMaterialAccessLevel.Structure);
        Assert.Single(Assert.IsType<ScenarioStructureProjection>(localSearch).Occurrences);
        using var json = JsonDocument.Parse(Serialize(localSearch));
        Assert.DoesNotContain(PropertyNames(json.RootElement), x => x is "textSearch" or "speakerSearch" or "text" or "speaker");
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "先行本文除外でも残るIDを保ち、訂正は版へ反映し本文検索/タグ除外の穴を接続せずDB cutを変えない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 連続本文照合と共通資料 (raw由来ID・訂正版・選択のread-time cutとDB境界不変)" }
    // fault = "contentの連番採番でIDがずれる、訂正後に古い一致を使う、または除外本文の前後を直結する"
    // observable = "全体/選択のIDと訂正版、本文検索/タグ除外の両権限JSON/辺、読取前後の保存cut"
    // observation_boundary = "public-boundary"
    // scope = "stable raw-derived identifiers and selected observed edges"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task StableIdsSurviveUnrelatedSelectionAndEditsDoNotBridgeGaps()
    {
        var game = await GameAsync();
        var common = Lines.Take(3).ToArray();
        var first = await VisitAsync(game, new[] { "「無関係な先行本文です」" }.Concat(common).ToArray());
        var second = await VisitAsync(game, common);
        var range = await _store.SaveCaptureRangeAsync(game, "common", new(first, 2, 0), new(second, 3, common[2].Length));
        var before = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(game, new(), ScenarioMaterialAccessLevel.Full));
        var selected = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(game, new(RangeId: range), ScenarioMaterialAccessLevel.Full));
        Assert.Equal(before.Version, selected.Version);
        foreach (var content in selected.Contents) Assert.Equal(before.Contents.Single(x => x.Text == content.Text).Id, content.Id);
        foreach (var occurrence in selected.Occurrences) Assert.Equal(before.Occurrences.Single(x => x.Source == occurrence.Source).Id, occurrence.Id);
        var item = (await _store.GetCaptureItemsAsync(first, 0))[1];
        await _store.SetCaptureLabelsAsync(first, [item.Id], null);
        var edited = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(game, new(RangeId: range), ScenarioMaterialAccessLevel.Full));
        Assert.True(edited.Version.Revision > selected.Version.Revision);
        Assert.True(edited.Version.MatchingRevision > selected.Version.MatchingRevision);
        Assert.Empty(edited.Matches); Assert.Equal(6, edited.Contents.Count);
        Assert.Equal(selected.Occurrences.Select(x => x.Id), edited.Occurrences.Select(x => x.Id));
        var structure = await _store.GetScenarioProjectionAsync(game, new(RangeId: range), ScenarioMaterialAccessLevel.Structure);
        var stripped = JsonNode.Parse(Serialize(edited)); RemoveContent(stripped);
        Assert.True(JsonNode.DeepEquals(stripped, JsonNode.Parse(Serialize(structure))));
        await _store.SaveCaptureRangeAsync(game, "left", new(second, 1, 0), new(second, 1, common[0].Length), ["sparse"]);
        await _store.SaveCaptureRangeAsync(game, "right", new(second, 3, 0), new(second, 3, common[2].Length), ["sparse"]);
        var sparse = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game,
            new(IncludeTags: ["sparse"]), ScenarioMaterialAccessLevel.Structure));
        Assert.Equal(2, sparse.Occurrences.Count); Assert.Empty(sparse.Graph.Edges);
        string[] holeLines = ["「穴選択_残す最初の長い本文です」", "「除外する真ん中の長い本文です」", "「穴選択_残す三番目の長い本文です」"];
        var holeSession = await VisitAsync(game, holeLines);
        await _store.SaveCaptureRangeAsync(game, "excluded middle", new(holeSession, 2, 0), new(holeSession, 2, holeLines[1].Length), ["excluded"]);
        var savedCuts = (await _store.GetScenarioFlowOverridesAsync(game)).Cuts.ToArray();
        foreach (var condition in new[] { new ScenarioArchiveSelection(TextSearch: "穴選択"), new ScenarioArchiveSelection(ExcludeTags: ["excluded"]) })
        {
            var fullHole = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(game, condition, ScenarioMaterialAccessLevel.Full));
            var structureHole = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game, condition, ScenarioMaterialAccessLevel.Structure));
            var holeOccurrences = fullHole.Occurrences.Where(x => x.Source.SessionId == holeSession.ToString()).ToArray();
            Assert.Equal(new long[] { 1, 3 }, holeOccurrences.Select(x => x.Source.ReceiveSequence));
            Assert.DoesNotContain(fullHole.Graph.Edges.SelectMany(x => x.Traversals), x => x.SessionId == holeSession.ToString());
            var allowed = JsonNode.Parse(Serialize(fullHole)); RemoveContent(allowed);
            Assert.True(JsonNode.DeepEquals(allowed, JsonNode.Parse(Serialize(structureHole))));
            Assert.Equal(fullHole.Version, structureHole.Version);
            Assert.True(fullHole.SelectionVersion.SameSelection(structureHole.SelectionVersion));
            Assert.Equal(savedCuts, (await _store.GetScenarioFlowOverridesAsync(game)).Cuts);
        }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "画像読取の共通検証は全文権限・同版/選択結果・正しい出現と画像関連を必要としgraph内の分割出現も同じ原文へ解決する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 連続本文照合と共通資料 (画像gate内のFull/版/scope/exact関連検証・graph分割出現)" }
    // fault = "構造権限や別出現/別画像/旧版/別scopeからの画像迂回を許すか有効なgraph部分出現を拒否する"
    // observable = "実snapshotに対する検証結果/例外と解決した原文key"
    // observation_boundary = "public-boundary"
    // scope = "common image authorization before gated byte reading"
    // lifecycle = "permanent"
    // risk_tags = ["authorization", "privacy"]
    // @end-test-value
    [Fact]
    public async Task ImageValidationRequiresFullCurrentSelectionAndExactAssociation()
    {
        var fixture = await FixtureAsync();
        await _store.SetScenarioFlowCutAsync(fixture.Game, new(fixture.Second, 4, 3));
        var selection = new ScenarioArchiveSelection(IncludeTags: ["second"]);
        var snapshot = await _store.GetScenarioMaterialAsync(fixture.Game, selection);
        var full = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(fixture.Game, selection, ScenarioMaterialAccessLevel.Full));
        var occurrence = full.Occurrences.Single(x => x.Source.ReceiveSequence == 4);
        var request = new ScenarioMaterialImageRequest(fixture.Game, selection, full.Version, full.SelectionVersion, occurrence.Id, fixture.SelectedImage);
        Assert.Equal(new CaptureEventKey(fixture.Second, 4), ScenarioMaterialImageAccess.Validate(snapshot, ScenarioMaterialAccessLevel.Full, request));
        Assert.Throws<UnauthorizedAccessException>(() => ScenarioMaterialImageAccess.Validate(snapshot, ScenarioMaterialAccessLevel.Structure, request));
        Assert.Throws<UnauthorizedAccessException>(() => ScenarioMaterialImageAccess.Validate(snapshot, (ScenarioMaterialAccessLevel)9, request));
        Assert.Throws<UnauthorizedAccessException>(() => ScenarioMaterialImageAccess.Validate(snapshot, ScenarioMaterialAccessLevel.Full,
            request with { OccurrenceId = full.Occurrences[0].Id }));
        Assert.Throws<UnauthorizedAccessException>(() => ScenarioMaterialImageAccess.Validate(snapshot, ScenarioMaterialAccessLevel.Full,
            request with { ImageId = fixture.OutsideImage }));
        Assert.Throws<InvalidOperationException>(() => ScenarioMaterialImageAccess.Validate(snapshot, ScenarioMaterialAccessLevel.Full,
            request with { SelectionVersion = new([], []) }));
        var graphPart = full.Graph.Occurrences.Single(x => x.Source.ReceiveSequence == 4 && x.Source.StartUtf16 == 3);
        Assert.Equal(new CaptureEventKey(fixture.Second, 4), ScenarioMaterialImageAccess.Validate(snapshot, ScenarioMaterialAccessLevel.Full,
            request with { OccurrenceId = graphPart.Id }));
        await _store.SetCaptureSpeakerAsync(fixture.Second, (await _store.GetCaptureItemsAsync(fixture.Second, 0))[0].Id, "訂正後の話者");
        var changed = await _store.GetScenarioMaterialAsync(fixture.Game, selection);
        Assert.Throws<InvalidOperationException>(() => ScenarioMaterialImageAccess.Validate(changed, ScenarioMaterialAccessLevel.Full, request));
    }

    public void Dispose()
    {
        _store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true);
    }
}
