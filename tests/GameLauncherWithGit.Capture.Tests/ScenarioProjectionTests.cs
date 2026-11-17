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
    // claim = "構造/完全の実JSONは同一版とIDを持ち、構造側は本文と個別話者のpropertyを省き区間話者集計とflow/範囲/分類/画像metadataを保つ"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 連続本文照合と共通資料 (共通projection・raw由来ID・Structureのproperty省略)" }
    // fault = "base型serializationで内部完全snapshotを混ぜる、機密値をnullに置く、または権限でID/版を採番し直す"
    // observable = "両公開projectionのJSON、再帰property名、本文/個別話者propertyの不在と区間話者件数、flow/範囲/画像IDと版"
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
        Assert.DoesNotContain("機密本文", decodedStructure);
        Assert.All(structure.Ranges, range => Assert.Equal(new ScenarioRangeSpeakerCount(Speaker, 7), Assert.Single(range.SpeakerCounts)));
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
            request with { SelectionVersion = new([], [], [], []) }));
        var graphPart = full.Graph.Occurrences.Single(x => x.Source.ReceiveSequence == 4 && x.Source.StartUtf16 == 3);
        Assert.Equal(new CaptureEventKey(fixture.Second, 4), ScenarioMaterialImageAccess.Validate(snapshot, ScenarioMaterialAccessLevel.Full,
            request with { OccurrenceId = graphPart.Id }));
        await _store.SetCaptureSpeakerAsync(fixture.Second, (await _store.GetCaptureItemsAsync(fixture.Second, 0))[0].Id, "訂正後の話者");
        var changed = await _store.GetScenarioMaterialAsync(fixture.Game, selection);
        Assert.Throws<InvalidOperationException>(() => ScenarioMaterialImageAccess.Validate(changed, ScenarioMaterialAccessLevel.Full, request));
    }

    // @test-value v2
    // kind = "contract"
    // claim = "全域では本文のない独立場面と未使用タグを保持し、有効な絞込みでは交差しない場面を除き、空の選択結果も場面/タグscopeで区別する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動場面フロー (全域と絞込み・明示metadata)" }
    // fault = "本文0件で全場面を消す、空白条件を絞込みとする、または場面/タグIDをscope比較から落とす"
    // observable = "空履歴の両tier JSON、正規化条件別のscene/tag配列とSelectionVersion値比較"
    // observation_boundary = "consumer"
    // scope = "manual scene selection without source occurrences"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task EmptyScenesAndExplicitTagsSurviveOnlyAnEffectivelyUnfilteredSelection()
    {
        var game = await GameAsync();
        var tag = await _store.GetOrCreateScenarioGameTagAsync(game, "unused explicit tag");
        var scene = await _store.CreateScenarioSceneAsync(game, new("Independent", "01", "Chapter", 10, 20, [], []));
        var whole = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game, new(), ScenarioMaterialAccessLevel.Structure));
        Assert.Empty(whole.Occurrences); Assert.Empty(whole.Ranges);
        Assert.Equal(scene.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(whole.SceneFlow.Scenes).Id);
        Assert.Equal(tag.ToString(System.Globalization.CultureInfo.InvariantCulture), Assert.Single(whole.SceneFlow.Tags).Id);
        var normalized = await _store.GetScenarioProjectionAsync(game,
            new(TextSearch: "  ", SpeakerSearch: "\t", IncludeTags: [], ExcludeTags: []), ScenarioMaterialAccessLevel.Structure);
        Assert.Equal(Serialize(whole), Serialize(normalized));
        var filtered = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game,
            new(IncludeTags: ["unused explicit tag"]), ScenarioMaterialAccessLevel.Structure));
        Assert.Empty(filtered.SceneFlow.Scenes); Assert.Empty(filtered.SceneFlow.Tags);
        Assert.Empty(filtered.Occurrences); Assert.Empty(filtered.Ranges);
        Assert.False(whole.SelectionVersion.SameSelection(filtered.SelectionVersion));
        Assert.False(whole.SelectionVersion.SameSelection(whole.SelectionVersion with { SceneIds = [] }));
        Assert.False(whole.SelectionVersion.SameSelection(whole.SelectionVersion with { TagIds = [] }));
        var copy = JsonSerializer.Deserialize<ScenarioMaterialSelectionVersion>(JsonSerializer.Serialize(whole.SelectionVersion, Json), Json)!;
        Assert.True(whole.SelectionVersion.SameSelection(copy));
        var full = await _store.GetScenarioProjectionAsync(game, new(), ScenarioMaterialAccessLevel.Full);
        Assert.Equal(Serialize(whole), Serialize(full));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "場面の複数区間参照を選択済raw区間へclipし、除外中間場面を補間せず明示manual辺だけを残し両権限で同じmetadataを返す"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動場面フロー (選択交差・manualとobserved・明示タグ公開)" }
    // fault = "場面が選択外区間/画像を連れ戻す、除外場面を跨ぐ辺を捏造する、または話者を自動tagmaster化する"
    // observable = "部分UTF16のrange.spans、scene RangeIds/TagIds、manual/observed辺と画像、実JSON禁止fieldと明示タグ名"
    // observation_boundary = "consumer"
    // scope = "selected manual flow and privacy projection"
    // lifecycle = "permanent"
    // impact = "構造権限と選択範囲を守り手編集と観測結果を混同しない"
    // distinction = "同tx DB読取から公開JSONまでの交差と実関連は型やschemaだけでは検証できない"
    // @end-test-value
    [Fact]
    public async Task SelectedScenesClipSourcesAndKeepOnlyExplicitManualConnections()
    {
        var game = await GameAsync();
        var session = await VisitAsync(game, Lines.Take(3).ToArray());
        var left = await _store.SaveCaptureRangeAsync(game, "left", new(session, 1, 2), new(session, 1, 7), ["keep"]);
        var middle = await _store.SaveCaptureRangeAsync(game, "middle", new(session, 2, 0), new(session, 2, Lines[1].Length), ["omit"]);
        var right = await _store.SaveCaptureRangeAsync(game, "right", new(session, 3, 0), new(session, 3, Lines[2].Length), ["keep"]);
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), new(1, 1, new string('c', 64), [3]), DateTimeOffset.UtcNow);
        var outside = await _store.SaveScreenshotAsync(new(game, new(session, 2)), new(1, 1, new string('d', 64), [4]), DateTimeOffset.UtcNow);
        var explicitTag = await _store.GetOrCreateScenarioGameTagAsync(game, "scene tag");
        await _store.GetOrCreateScenarioGameTagAsync(game, "unused");
        var a = await _store.CreateScenarioSceneAsync(game, new("A", "1", "Opening", 0, 0, [left, middle], [explicitTag]));
        var b = await _store.CreateScenarioSceneAsync(game, new("B", "1", null, 100, 0, [middle], []));
        var c = await _store.CreateScenarioSceneAsync(game, new("C", "3", null, 200, 0, [right], []));
        await _store.SetScenarioSceneConnectionAsync(game, a, b, true);
        await _store.SetScenarioSceneConnectionAsync(game, b, c, true);
        var selection = new ScenarioArchiveSelection(IncludeTags: ["keep"]);
        var selected = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Structure));
        Assert.Equal(new[] { a.ToString(), c.ToString() }, selected.SceneFlow.Scenes.Select(x => x.Id));
        Assert.Equal(new[] { left.ToString() }, selected.SceneFlow.Scenes[0].RangeIds);
        Assert.Equal(new[] { explicitTag.ToString() }, selected.SceneFlow.Scenes[0].TagIds);
        Assert.Equal(new[] { "keep", "scene tag" }, selected.SceneFlow.Tags.Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.All(selected.SceneFlow.RangeTags, link => Assert.Contains(selected.Ranges, range => range.Id == link.RangeId));
        Assert.Empty(selected.SceneFlow.Connections); Assert.Empty(selected.Graph.Edges);
        var span = Assert.Single(selected.Ranges.Single(x => x.Id == left.ToString()).Spans);
        Assert.Equal(2, span.StartUtf16); Assert.Equal(5, span.LengthUtf16);
        Assert.Equal(image.ImageId, Assert.Single(selected.Images).Id);
        Assert.DoesNotContain(selected.Images, x => x.Id == outside.ImageId);
        Assert.DoesNotContain(selected.SceneFlow.Tags, x => x.Name == Speaker);
        var speakerTag = await _store.GetOrCreateScenarioGameTagAsync(game, Speaker);
        await _store.UpdateScenarioSceneAsync(game, a, new("A", "1", "Opening", 0, 0, [left, middle], [explicitTag, speakerTag]));
        await _store.SetScenarioSceneConnectionAsync(game, a, c, true);
        await _store.SetScenarioSceneConnectionAsync(game, c, a, true);
        await _store.SetScenarioSceneConnectionAsync(game, a, a, true);
        selected = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Structure));
        Assert.Equal(3, selected.SceneFlow.Connections.Count);
        Assert.Contains(new ScenarioMaterialSceneConnection(a.ToString(), c.ToString()), selected.SceneFlow.Connections);
        Assert.Contains(new ScenarioMaterialSceneConnection(c.ToString(), a.ToString()), selected.SceneFlow.Connections);
        Assert.Contains(new ScenarioMaterialSceneConnection(a.ToString(), a.ToString()), selected.SceneFlow.Connections);
        Assert.Empty(selected.Graph.Edges);
        Assert.Contains(selected.SceneFlow.Tags, x => x.Name == Speaker);
        using var json = JsonDocument.Parse(Serialize(selected));
        Assert.DoesNotContain(PropertyNames(json.RootElement), name => new[] { "text", "speaker", "preview", "bytes", "path", "filePath", "thumbnail" }.Contains(name));
        var full = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Full));
        Assert.Equal(JsonSerializer.Serialize(selected.SceneFlow, Json), JsonSerializer.Serialize(full.SceneFlow, Json));
        Assert.Equal(selected.Version, full.Version); Assert.True(selected.SelectionVersion.SameSelection(full.SelectionVersion));
        var sceneOnlyTag = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game,
            new(IncludeTags: ["scene tag"]), ScenarioMaterialAccessLevel.Structure));
        Assert.Empty(sceneOnlyTag.SceneFlow.Scenes);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "場面metadata変更はIDと照合完了を維持し旧版画像を失効、選択scopeの場面/タグ列改竄を拒否し、区間削除後も同場面を空で保持する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動場面フロー (資料版・選択scope・削除と画像参照)" }
    // fault = "名称/配置をIDにする、metadata変更が照合を再構築する、画像gateが旧版や場面/タグscope差を無視する、または削除区間を別本文へ付替える"
    // observable = "再投影した版/MatchingComplete/安定IDと画像検証例外、区間削除後の全域/絞込み場面"
    // observation_boundary = "public-boundary"
    // scope = "scene edits, material revision and image scope validation"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task SceneMetadataInvalidatesImageVersionWithoutRebuildingMatchingOrReplacingSources()
    {
        var game = await GameAsync(); var session = await VisitAsync(game, Lines.Take(3).ToArray());
        await _store.EndCaptureSessionAsync(session);
        var range = await _store.SaveCaptureRangeAsync(game, "source", new(session, 1, 0), new(session, 1, Lines[0].Length), ["range tag"]);
        var tag = await _store.GetOrCreateScenarioGameTagAsync(game, "explicit");
        var edit = new ScenarioSceneEdit("Original", "01", null, 1, 2, [range], [tag]);
        var scene = await _store.CreateScenarioSceneAsync(game, edit);
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), new(1, 1, new string('e', 64), [5]), DateTimeOffset.UtcNow);
        var selection = new ScenarioArchiveSelection(RangeId: range);
        var initial = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Full));
        Assert.True(initial.MatchingComplete);
        var request = new ScenarioMaterialImageRequest(game, selection, initial.Version, initial.SelectionVersion, initial.Occurrences[0].Id, image.ImageId);
        foreach (var mutate in new Func<Task>[] {
            () => _store.UpdateScenarioSceneAsync(game, scene, edit with { Title = "Renamed", DisplayNumber = "99", X = -30, Y = 42 }),
            () => _store.RenameScenarioGameTagAsync(game, tag, "renamed tag"),
            () => _store.SetScenarioSceneConnectionAsync(game, scene, scene, true) })
        {
            await mutate();
            var current = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Full));
            Assert.True(current.Version.Revision > request.Version.Revision);
            Assert.Equal(initial.Version.MatchingRevision, current.Version.MatchingRevision); Assert.True(current.MatchingComplete);
            Assert.Equal(initial.SceneFlow.Scenes[0].Id, Assert.Single(current.SceneFlow.Scenes).Id);
            var snapshot = await _store.GetScenarioMaterialAsync(game, selection);
            Assert.Throws<InvalidOperationException>(() => ScenarioMaterialImageAccess.Validate(snapshot, ScenarioMaterialAccessLevel.Full, request));
            request = request with { Version = current.Version, SelectionVersion = current.SelectionVersion };
            Assert.Equal(new CaptureEventKey(session, 1), ScenarioMaterialImageAccess.Validate(snapshot, ScenarioMaterialAccessLevel.Full, request));
        }
        var latest = await _store.GetScenarioMaterialAsync(game, selection);
        foreach (var scope in new[] { request.SelectionVersion with { SceneIds = [] }, request.SelectionVersion with { TagIds = [] } })
            Assert.Throws<InvalidOperationException>(() => ScenarioMaterialImageAccess.Validate(latest, ScenarioMaterialAccessLevel.Full, request with { SelectionVersion = scope }));
        await _store.DeleteCaptureRangeAsync(game, range);
        var whole = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game, new(), ScenarioMaterialAccessLevel.Structure));
        Assert.Equal(initial.SceneFlow.Scenes[0].Id, Assert.Single(whole.SceneFlow.Scenes).Id);
        Assert.Empty(whole.SceneFlow.Scenes[0].RangeIds); Assert.Empty(whole.SceneFlow.RangeTags);
        Assert.Equal(3, whole.Occurrences.Count);
        var filtered = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game, new(IncludeTags: ["range tag"]), ScenarioMaterialAccessLevel.Structure));
        Assert.Empty(filtered.SceneFlow.Scenes); Assert.Empty(filtered.Occurrences); Assert.Empty(filtered.Images);
    }

    // @test-value v2
    // kind = "contract"
    // claim = "区間集計は選択との実交差内のDialogue itemを一度だけ数え、分類分割と同文別出現を別件にし未確定話者/非Dialogue/空区間と対象外を区別する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 区間話者集計" }
    // fault = "clipや句読点で水増しする、同文/同イベントを統合する、区間外や非Dialogueを混入する、未確定話者を人物補完する"
    // observable = "同tx読取のrange別SpeakerCounts/UnknownSpeakerDialogueCount、選択clip数、両tier JSONと版、原文/タグ不変、削除後の0件と対象外"
    // observation_boundary = "consumer"
    // scope = "selected range Dialogue item aggregation"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task RangeSpeakerCountsUseDistinctRecordedDialogueItemsWithinSelectedIntersections()
    {
        var game = await GameAsync();
        var dialogue = (await _store.GetCaptureLabelsAsync(game)).Single(x => x.Kind == CaptureKind.Dialogue).Id;
        var narration = (await _store.GetCaptureLabelsAsync(game)).Single(x => x.Kind == CaptureKind.Narration).Id;
        long other;
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO CaptureLabels(CaptureGameId,Name,Kind) VALUES($game,'legacy other',2) RETURNING Id";
            command.Parameters.AddWithValue("$game", game);
            other = (long)(await command.ExecuteScalarAsync())!;
        }
        string[] parts = ["「同じセリフ。続く句！」", "「同じセリフ。続く句！」", "地の文", "未判定", "その他", "「話者未確定」", "「表記違い」"];
        var raw = string.Concat(parts);
        var first = await VisitAsync(game, [raw]);
        var boundaries = parts.Select((_, index) => parts.Take(index + 1).Sum(x => x.Length)).SkipLast(1).ToArray();
        await _store.SplitCaptureEventAsync(first, 1, boundaries,
            [new(dialogue, "Alice"), new(dialogue, "Alice"), new(narration, "not dialogue"), new(null, "not dialogue"),
             new(other, "not dialogue"), new(dialogue, null), new(dialogue, "ALICE")]);
        var second = await VisitAsync(game, [parts[0]]);
        await _store.SetCaptureSpeakerAsync(second, Assert.Single(await _store.GetCaptureItemsAsync(second, 0)).Id, "Alice");
        var wholeId = await _store.SaveCaptureRangeAsync(game, "whole", new(first, 1, 0), new(second, 1, parts[0].Length));
        var revisitId = await _store.SaveCaptureRangeAsync(game, "revisit", new(second, 1, 0), new(second, 1, parts[0].Length));
        var holeId = await _store.SaveCaptureRangeAsync(game, "hole", new(first, 1, 3), new(first, 1, 6), ["hole"]);
        var offset = parts[0].Length + parts[1].Length;
        for (var index = 2; index <= 5; index++)
        {
            await _store.SaveCaptureRangeAsync(game, $"part-{index}", new(first, 1, offset), new(first, 1, offset + parts[index].Length));
            offset += parts[index].Length;
        }
        var initial = await _store.GetScenarioMaterialAsync(game, new());
        var tags = (await _store.GetScenarioSceneFlowAsync(game)).Tags.ToArray();
        foreach (var selection in new[] { new ScenarioArchiveSelection(), new ScenarioArchiveSelection(ExcludeTags: ["hole"]) })
        {
            var full = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Full));
            var structure = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Structure));
            var range = full.Ranges.Single(x => x.Id == wholeId.ToString());
            Assert.Equal(new[] { new ScenarioRangeSpeakerCount("ALICE", 1), new ScenarioRangeSpeakerCount("Alice", 3) }, range.SpeakerCounts);
            Assert.Equal(1, range.UnknownSpeakerDialogueCount);
            Assert.All(full.Ranges.Where(x => x.Name is "part-2" or "part-3" or "part-4"), x =>
            { Assert.Empty(x.SpeakerCounts); Assert.Equal(0, x.UnknownSpeakerDialogueCount); Assert.NotEmpty(x.Spans); });
            var unknown = full.Ranges.Single(x => x.Name == "part-5");
            Assert.Empty(unknown.SpeakerCounts); Assert.Equal(1, unknown.UnknownSpeakerDialogueCount);
            if (selection.ExcludeTags is not null)
            {
                Assert.DoesNotContain(full.Ranges, x => x.Id == holeId.ToString());
                Assert.Equal(9, full.Occurrences.Count); // seven stored items, one revisit, and an extra clip of the first item
                Assert.Equal(3, full.Occurrences.Count(x => x.Source.SessionId == first.ToString() && x.Source.StartUtf16 < parts[0].Length * 2));
            }
            Assert.Equal(JsonSerializer.Serialize(full.Ranges, Json), JsonSerializer.Serialize(structure.Ranges, Json));
            Assert.Equal(full.Version, structure.Version); Assert.True(full.SelectionVersion.SameSelection(structure.SelectionVersion));
        }
        foreach (var selection in new[] { new ScenarioArchiveSelection(TextSearch: "表記違い"), new ScenarioArchiveSelection(SpeakerSearch: "ALICE") })
        {
            var filtered = Assert.IsType<ScenarioFullProjection>(await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Full));
            Assert.Equal(0, filtered.Ranges.Single(x => x.Id == wholeId.ToString()).UnknownSpeakerDialogueCount);
            Assert.DoesNotContain(filtered.Ranges.SelectMany(x => x.SpeakerCounts), x => x.SpeakerName == "not dialogue");
            if (selection.TextSearch is not null)
                Assert.Equal(new ScenarioRangeSpeakerCount("ALICE", 1), Assert.Single(Assert.Single(filtered.Ranges).SpeakerCounts));
            else
                Assert.Equal(new[] { new ScenarioRangeSpeakerCount("ALICE", 1), new ScenarioRangeSpeakerCount("Alice", 3) },
                    filtered.Ranges.Single(x => x.Id == wholeId.ToString()).SpeakerCounts);
        }
        var unclassified = Assert.IsType<ScenarioStructureProjection>(await _store.GetScenarioProjectionAsync(game,
            new(UnclassifiedOnly: true), ScenarioMaterialAccessLevel.Structure));
        Assert.All(unclassified.Ranges, x => { Assert.Empty(x.SpeakerCounts); Assert.Equal(0, x.UnknownSpeakerDialogueCount); });
        Assert.Equal(initial.Version, (await _store.GetScenarioMaterialAsync(game, new())).Version);
        Assert.Equal(raw, (await _store.GetCaptureItemsAsync(first, 0))[0].RawText);
        Assert.Equal(tags, (await _store.GetScenarioSceneFlowAsync(game)).Tags);
        await _store.DeleteCaptureEventsAsync(game, [new(second, 1)]);
        foreach (var level in new[] { ScenarioMaterialAccessLevel.Structure, ScenarioMaterialAccessLevel.Full })
        {
            var current = await _store.GetScenarioProjectionAsync(game, new(), level);
            var currentRanges = current is ScenarioStructureProjection structure ? structure.Ranges : ((ScenarioFullProjection)current).Ranges;
            var empty = currentRanges.Single(x => x.Id == revisitId.ToString());
            Assert.Empty(empty.Spans); Assert.Empty(empty.SpeakerCounts); Assert.Equal(0, empty.UnknownSpeakerDialogueCount);
            var filtered = await _store.GetScenarioProjectionAsync(game, new(RangeId: revisitId), level);
            Assert.Empty(filtered is ScenarioStructureProjection s ? s.Ranges : ((ScenarioFullProjection)filtered).Ranges);
        }
    }

    public void Dispose()
    {
        _store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true);
    }
}
