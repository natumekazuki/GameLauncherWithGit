using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using System.Text.Json;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Mcp;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace GameLauncherWithGit.Mcp.Tests;

public sealed class ScenarioMcpTests
{
    // @test-value v2
    // kind = "invariant"
    // claim = "全HTTP入口は欠落・不正キーと非loopback Host/Originを拒否する"
    // oracle = { type = "contract", ref = "docs/design/scenario-mcp.md: 認証と二権限" }
    // fault = "初期化やGET/DELETEだけ認証を迂回する、または任意Host/Originを許可する"
    // observable = "実loopback HTTP応答の401/403と秘密を含まない本文"
    // observation_boundary = "public-boundary"
    // scope = "MCP HTTP authentication"
    // lifecycle = "permanent"
    // risk_tags = ["authentication", "privacy"]
    // @end-test-value
    [Fact]
    public async Task EveryHttpMethodAuthenticatesAndRejectsForeignOrigins()
    {
        await using var fixture = await Fixture.Create();
        using var http = new HttpClient();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        foreach (var key in new string?[] { null, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) })
        {
            using var request = new HttpRequestMessage(method, fixture.Host.Endpoint);
            if (key is not null) request.Headers.Authorization = new("Bearer", key);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("", await response.Content.ReadAsStringAsync());
        }
        foreach (var header in new[] { "Host", "Origin" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Host.Endpoint);
            request.Headers.Authorization = new("Bearer", fixture.Keys.FullKey);
            request.Headers.TryAddWithoutValidation(header, header == "Host" ? "untrusted.example" : "https://untrusted.example");
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "既存の作品IDは従来の記録を保ち、枝の資料は別の記録範囲IDからのみ取得できる"
    // oracle = { type = "contract", ref = "docs/design/scenario-mcp.md: ブランチ別の記録を読む" }
    // fault = "枝別のIDを作品一覧へ混ぜる、従来の記録へ枝の本文を混ぜる、別の枝の版を受け入れる"
    // observable = "公式SDKで取得した作品と範囲のID、各範囲の本文、別の枝の資料版を使った要求のエラー"
    // observation_boundary = "consumer"
    // scope = "MCP branch history selection"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task HistoryScopeIdsSelectEachBranchWithoutChangingTheLegacyId()
    {
        await using var fixture = await Fixture.Create();
        var repo = Path.Combine(fixture.DirectoryPath, "sample-repository");
        var alpha = await fixture.Store.GetOrCreateBranchRecordIdAsync(fixture.Game, repo, "refs/heads/alpha");
        var beta = await fixture.Store.GetOrCreateBranchRecordIdAsync(fixture.Game, repo, "refs/heads/beta");
        foreach (var (id, text) in new[] { (alpha, "Alpha branch body"), (beta, "Beta branch body") })
        {
            var session = await fixture.Store.BeginCaptureSessionAsync(id);
            var source = new CaptureSource(1, 0, 0, 0, "HOOK", "fixture");
            await fixture.Store.AppendCaptureAsync(session, new(1, DateTimeOffset.UtcNow, source, text, CaptureRole.Body), new());
            await fixture.Store.EndCaptureSessionAsync(session);
        }

        await using var full = await fixture.Client(fixture.Keys.FullKey);
        await using var structure = await fixture.Client(fixture.Keys.StructureKey);
        var original = fixture.Game.ToString(CultureInfo.InvariantCulture);
        var alphaId = alpha.ToString(CultureInfo.InvariantCulture);
        var betaId = beta.ToString(CultureInfo.InvariantCulture);
        var gameIds = (await Read(full, "scenario_games", new())).EnumerateArray()
            .Select(x => x.GetProperty("captureGameId").GetString()).ToArray();
        Assert.Contains(original, gameIds);
        Assert.DoesNotContain(alphaId, gameIds);
        Assert.DoesNotContain(betaId, gameIds);

        var scopes = await Read(structure, "scenario_history_scopes", new() { ["gameId"] = original });
        Assert.Equal(3, scopes.GetArrayLength());
        var byId = scopes.EnumerateArray().ToDictionary(x => x.GetProperty("captureGameId").GetString()!);
        Assert.Equal(JsonValueKind.Null, byId[original].GetProperty("branchRef").ValueKind);
        Assert.Equal("refs/heads/alpha", byId[alphaId].GetProperty("branchRef").GetString());
        Assert.Equal("refs/heads/beta", byId[betaId].GetProperty("branchRef").GetString());
        Assert.All(byId.Values, scope => Assert.True(
            scope.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["captureGameId", "branchRef"])));
        var otherScopes = await Read(structure, "scenario_history_scopes", new()
        { ["gameId"] = fixture.OtherGame.ToString(CultureInfo.InvariantCulture) });
        Assert.Equal(JsonValueKind.Null,
            Assert.Single(otherScopes.EnumerateArray()).GetProperty("branchRef").ValueKind);

        var legacy = await Read(full, "scenario_snapshot", new() { ["gameId"] = original });
        Assert.Equal(new[] { "SECRET_BODY_A", "SECRET_BODY_B" },
            legacy.GetProperty("contents").EnumerateArray().Select(x => x.GetProperty("text").GetString()));
        var alphaData = await Read(full, "scenario_snapshot", new() { ["gameId"] = alphaId });
        var betaData = await Read(full, "scenario_export_json", new() { ["gameId"] = betaId });
        Assert.Equal("Alpha branch body",
            Assert.Single(alphaData.GetProperty("contents").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("Beta branch body",
            Assert.Single(betaData.GetProperty("contents").EnumerateArray()).GetProperty("text").GetString());
        Assert.True((await full.CallToolAsync("scenario_snapshot", new Dictionary<string, object?>
        {
            ["gameId"] = betaId,
            ["expectedVersion"] = alphaData.GetProperty("version")
        })).IsError);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "二キーの実MCP応答とZIPは同じID/版を持ち構造keyではtool/resource/JSON経路から機密fieldを省く"
    // oracle = { type = "contract", ref = "docs/design/scenario-mcp.md: 認証と二権限; 共通 snapshot と参照" }
    // fault = "full snapshotを構造側でserializeする、caller levelを信じる、直接ID/resource/exportで認可が抜ける"
    // observable = "公式SDKで取得したJSONのID/版一致、機密property不在、Full本文、tool/resource公開一覧"
    // observation_boundary = "consumer"
    // scope = "MCP projection integration"
    // lifecycle = "permanent"
    // risk_tags = ["authorization", "privacy"]
    // @end-test-value
    [Fact]
    public async Task BothKeysUseSameIdsWithoutAlternatePathDisclosure()
    {
        await using var fixture = await Fixture.Create();
        await using var full = await fixture.Client(fixture.Keys.FullKey);
        await using var structure = await fixture.Client(fixture.Keys.StructureKey);
        var args = new Dictionary<string, object?> { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture), ["level"] = "Full" };
        var fullJson = await Read(full, "scenario_snapshot", args);
        var structureJson = await Read(structure, "scenario_snapshot", args);
        Assert.Equal(fullJson.GetProperty("version").GetRawText(), structureJson.GetProperty("version").GetRawText());
        Assert.Equal(fullJson.GetProperty("selectionVersion").GetRawText(), structureJson.GetProperty("selectionVersion").GetRawText());
        Assert.Equal("SECRET_BODY_A", fullJson.GetProperty("contents")[0].GetProperty("text").GetString());
        AssertStructure(structureJson);
        foreach (var collection in new[] { "contents", "occurrences", "images" })
            Assert.Equal(fullJson.GetProperty(collection).EnumerateArray().Select(x => x.GetProperty("id").GetString()),
                structureJson.GetProperty(collection).EnumerateArray().Select(x => x.GetProperty("id").GetString()));

        var version = fullJson.GetProperty("version");
        var occurrence = fullJson.GetProperty("occurrences")[0];
        var id = occurrence.GetProperty("id").GetString();
        var byId = new Dictionary<string, object?>(args) { ["collection"] = "occurrences", ["id"] = id, ["expectedVersion"] = version };
        AssertStructure(await Read(structure, "scenario_by_id", byId));
        var bySource = new Dictionary<string, object?> { ["source"] = occurrence.GetProperty("source"), ["expectedVersion"] = version };
        AssertStructure(await Read(structure, "scenario_by_source", bySource));
        AssertStructure(await Read(structure, "scenario_export_json", args));
        var revision = version.GetProperty("revision").GetInt64();
        var matching = version.GetProperty("matchingRevision").GetInt64();
        foreach (var uri in new[] { $"scenario://material/{fixture.Game}/{revision}/{matching}",
                     $"scenario://item/{fixture.Game}/{revision}/{matching}/occurrences/{Uri.EscapeDataString(id!)}" })
        {
            var resource = await structure.ReadResourceAsync(uri);
            AssertStructure(JsonDocument.Parse(Assert.IsType<TextResourceContents>(resource.Contents.Single()).Text).RootElement);
        }
        Assert.Contains(await structure.ListToolsAsync(), x => x.Name == "scenario_by_source");
        Assert.NotEmpty(await structure.ListResourceTemplatesAsync());
        foreach (var level in new[] { ScenarioMaterialAccessLevel.Structure, ScenarioMaterialAccessLevel.Full })
        {
            var path = Path.Combine(fixture.DirectoryPath, level + ".zip");
            await fixture.Store.ExportScenarioArchiveAsync(fixture.Game, new(), path, level);
            using var zip = ZipFile.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(zip.GetEntry("material.json")!.Open());
            var zipped = document.RootElement;
            var mcp = level == ScenarioMaterialAccessLevel.Full ? fullJson : structureJson;
            Assert.Equal(mcp.GetProperty("version").Deserialize<ScenarioMaterialWireVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                zipped.GetProperty("version").Deserialize<ScenarioMaterialWireVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            foreach (var collection in new[] { "contents", "occurrences", "images" })
                Assert.Equal(mcp.GetProperty(collection).EnumerateArray().Select(x => x.GetProperty("id").GetString()),
                    zipped.GetProperty(collection).EnumerateArray().Select(x => x.GetProperty("id").GetString()));
        }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ID/raw参照は作品・選択・資料版を越えて再利用できず、構造キーでは本文検索できない"
    // oracle = { type = "contract", ref = "docs/design/scenario-mcp.md: 共通 snapshot と参照; 認証と二権限" }
    // fault = "別作品IDや選択外raw参照を解決する、旧版を無視する、本文一致を構造キーへ漏らす"
    // observable = "実MCPの選択結果とIsError、訂正後の旧版エラー"
    // observation_boundary = "consumer"
    // scope = "MCP scope and version binding"
    // lifecycle = "permanent"
    // risk_tags = ["authorization"]
    // @end-test-value
    [Fact]
    public async Task SelectionVersionAndGameBoundariesRejectStaleOrForeignReferences()
    {
        await using var fixture = await Fixture.Create();
        await using var client = await fixture.Client(fixture.Keys.StructureKey);
        var snapshot = await Read(client, "scenario_snapshot", new() { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture) });
        var version = snapshot.GetProperty("version");
        var occurrence = snapshot.GetProperty("occurrences")[1];
        var selected = await Read(client, "scenario_snapshot", new() { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture), ["selection"] = new { rangeId = fixture.Range.ToString(CultureInfo.InvariantCulture) } });
        Assert.Single(selected.GetProperty("occurrences").EnumerateArray());
        Assert.NotEqual(snapshot.GetProperty("selectionVersion").GetRawText(), selected.GetProperty("selectionVersion").GetRawText());
        foreach (var args in new Dictionary<string, object?>[]
        {
            new() { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture), ["collection"] = "occurrences", ["id"] = occurrence.GetProperty("id"), ["expectedVersion"] = null },
            new() { ["gameId"] = fixture.OtherGame.ToString(CultureInfo.InvariantCulture), ["collection"] = "occurrences", ["id"] = occurrence.GetProperty("id"), ["expectedVersion"] = version },
            new() { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture), ["collection"] = "occurrences", ["id"] = occurrence.GetProperty("id"), ["expectedVersion"] = version, ["selection"] = new { rangeId = fixture.Range.ToString(CultureInfo.InvariantCulture) } }
        }) Assert.True((await client.CallToolAsync("scenario_by_id", args)).IsError);
        Assert.True((await client.CallToolAsync("scenario_by_source", new Dictionary<string, object?>
        { ["source"] = occurrence.GetProperty("source"), ["expectedVersion"] = version, ["selection"] = new { rangeId = fixture.Range.ToString(CultureInfo.InvariantCulture) } })).IsError);
        foreach (var search in new[] { "textSearch", "speakerSearch" })
            Assert.True((await client.CallToolAsync("scenario_snapshot", new Dictionary<string, object?>
            { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture), ["selection"] = new Dictionary<string, string> { [search] = "SECRET" } })).IsError);
        await using (var full = await fixture.Client(fixture.Keys.FullKey))
        {
            var searched = await Read(full, "scenario_snapshot", new() { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture), ["selection"] = new { textSearch = "SECRET_BODY_A" } });
            Assert.Single(searched.GetProperty("occurrences").EnumerateArray());
        }
        await fixture.Store.DeleteCaptureEventsAsync(fixture.Game, [new(fixture.Session, 1)]);
        Assert.True((await client.CallToolAsync("scenario_snapshot", new Dictionary<string, object?>
        { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture), ["expectedVersion"] = version })).IsError);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "Fullで開始したclientの次要求も現キーで再認可しsession/stream識別子で権限を復元しない"
    // oracle = { type = "contract", ref = "docs/design/scenario-mcp.md: 認証と二権限" }
    // fault = "初期化時のFull権限を保存する、旧sessionId/Last-Event-IDからFull応答を再生する"
    // observable = "同一SDK clientでキー変更後の構造JSONと無効キーHTTP拒否、再送GETの非開示"
    // observation_boundary = "consumer"
    // scope = "MCP request-scoped authority"
    // lifecycle = "permanent"
    // risk_tags = ["authentication", "authorization"]
    // @end-test-value
    [Fact]
    public async Task ReusedClientAndStreamHeadersCannotRetainFullAuthority()
    {
        await using var fixture = await Fixture.Create();
        using var handler = new ChangingKeyHandler(fixture.Keys.FullKey);
        using var http = new HttpClient(handler);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
        { Endpoint = fixture.Host.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp }, http, ownsHttpClient: false));
        var args = new Dictionary<string, object?> { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture) };
        var full = await Read(client, "scenario_snapshot", args);
        Assert.True(full.GetProperty("contents")[0].TryGetProperty("text", out _));
        Assert.Null(client.SessionId);
        handler.Key = fixture.Keys.StructureKey;
        handler.Replay = true;
        AssertStructure(await Read(client, "scenario_snapshot", args));
        using var replay = await http.GetAsync(fixture.Host.Endpoint);
        var replayBody = await replay.Content.ReadAsStringAsync();
        Assert.False(replay.IsSuccessStatusCode);
        Assert.DoesNotContain("SECRET", replayBody);
        handler.Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using (var rejected = await http.GetAsync(fixture.Host.Endpoint)) Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.CallToolAsync("scenario_snapshot", args));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "画像はFullかつ同作品/版/scope/出現関連だけ取得でき、structureの直接画像tool/resource/raw参照は拒否する"
    // oracle = { type = "contract", ref = "docs/design/scenario-mcp.md: 画像境界" }
    // fault = "画像IDだけでbytesを読む、scope/版を無視する、構造キーでもbinary resourceへ到達できる"
    // observable = "実SDK経由PNG bytes一致と不正要求のIsError/resource例外"
    // observation_boundary = "consumer"
    // scope = "MCP image gate integration"
    // lifecycle = "permanent"
    // risk_tags = ["authorization", "privacy"]
    // @end-test-value
    [Fact]
    public async Task ImageToolsAndResourcesEnforceTierVersionScopeAndAssociation()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Store.SetScenarioFlowCutAsync(fixture.Game, new(fixture.Session, 1, 6));
        await using var full = await fixture.Client(fixture.Keys.FullKey);
        await using var structure = await fixture.Client(fixture.Keys.StructureKey);
        var snapshot = await Read(full, "scenario_snapshot", new() { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture) });
        var version = snapshot.GetProperty("version").Deserialize<ScenarioMaterialWireVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var scope = snapshot.GetProperty("selectionVersion").Deserialize<ScenarioMaterialSelectionVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var occurrence = snapshot.GetProperty("occurrences")[0];
        var request = new ScenarioMcpImageRequest(fixture.Game.ToString(CultureInfo.InvariantCulture), new(), version, scope,
            occurrence.GetProperty("id").GetString()!, fixture.ImageId);
        var image = await full.CallToolAsync("scenario_image", new Dictionary<string, object?> { ["request"] = request });
        Assert.False(image.IsError == true);
        Assert.Equal(Fixture.Png, Assert.IsType<ImageContentBlock>(Assert.Single(image.Content)).DecodedData.ToArray());
        var rawArgs = new Dictionary<string, object?> { ["source"] = occurrence.GetProperty("source"), ["imageId"] = fixture.ImageId,
            ["expectedVersion"] = snapshot.GetProperty("version"), ["selectionVersion"] = scope };
        var rawImage = await full.CallToolAsync("scenario_image_by_source", rawArgs);
        Assert.False(rawImage.IsError == true);
        Assert.Equal(Fixture.Png, Assert.IsType<ImageContentBlock>(Assert.Single(rawImage.Content)).DecodedData.ToArray());
        var cutArgs = new Dictionary<string, object?>(rawArgs) { ["source"] = snapshot.GetProperty("graph").GetProperty("occurrences")[0].GetProperty("source") };
        var cutImage = await full.CallToolAsync("scenario_image_by_source", cutArgs);
        Assert.False(cutImage.IsError == true);
        Assert.Equal(Fixture.Png, Assert.IsType<ImageContentBlock>(Assert.Single(cutImage.Content)).DecodedData.ToArray());
        var uri = $"scenario://image/{fixture.Game}/{version.Revision}/{version.MatchingRevision}/{Uri.EscapeDataString(request.OccurrenceId)}/{fixture.ImageId}";
        var resource = await full.ReadResourceAsync(uri);
        Assert.Equal(Fixture.Png, Assert.IsType<BlobResourceContents>(Assert.Single(resource.Contents)).DecodedData.ToArray());
        Assert.True((await structure.CallToolAsync("scenario_image", new Dictionary<string, object?> { ["request"] = request, ["level"] = "Full" })).IsError);
        Assert.True((await structure.CallToolAsync("scenario_image_by_source", rawArgs)).IsError);
        await Assert.ThrowsAsync<McpProtocolException>(async () => await structure.ReadResourceAsync(uri));
        var selectedSnapshot = await Read(full, "scenario_snapshot", new() { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture), ["selection"] = new { rangeId = fixture.Range.ToString(CultureInfo.InvariantCulture) } });
        var selectedScope = selectedSnapshot.GetProperty("selectionVersion").Deserialize<ScenarioMaterialSelectionVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var invalid in new[]
        {
            request with { CaptureGameId = fixture.OtherGame.ToString(CultureInfo.InvariantCulture) },
            request with { ImageId = fixture.OtherImageId },
            request with { ImageId = fixture.UnrelatedImageId },
            request with { Version = version with { Revision = version.Revision + 1 } },
            request with { Version = version with { MatchingRevision = version.MatchingRevision + 1 } },
            request with { SelectionVersion = new([], [], [], []) },
            request with { Selection = new(RangeId: fixture.Range.ToString(CultureInfo.InvariantCulture)), SelectionVersion = selectedScope, OccurrenceId = snapshot.GetProperty("occurrences")[1].GetProperty("id").GetString()! }
        }) Assert.True((await full.CallToolAsync("scenario_image", new Dictionary<string, object?> { ["request"] = invalid })).IsError);
        await fixture.Store.DeleteCaptureEventsAsync(fixture.Game, [new(fixture.Session, 1)]);
        Assert.True((await full.CallToolAsync("scenario_image", new Dictionary<string, object?> { ["request"] = request })).IsError);
    }

    // @test-value v2
    // kind = "contract"
    // claim = "設定生成/ローテート/無効化は秘密を表示せず次のlistener起動でだけキーを入れ替える"
    // oracle = { type = "contract", ref = "docs/design/scenario-mcp.md: 設定と寿命" }
    // fault = "旧キーが再起動後も通る、同じキーで両権限を生成する、無効化でlistenする、scriptが秘密を出す"
    // observable = "合成設定の検証、script exit/stdout/stderr、旧/新キーHTTP statusと無効listener Endpoint"
    // observation_boundary = "public-boundary"
    // scope = "MCP configuration lifecycle"
    // lifecycle = "permanent"
    // risk_tags = ["authentication", "privacy"]
    // @end-test-value
    [Fact]
    public async Task ConfigurationScriptRotatesKeysWithoutPrintingAndRestartAppliesThem()
    {
        await using var fixture = await Fixture.Create();
        var path = Path.Combine(fixture.DirectoryPath, "scenario-mcp.json");
        Assert.False(ScenarioMcpConfiguration.Load(path).Enabled);
        var firstOutput = await Configure(fixture.DirectoryPath, "Enable");
        var first = ScenarioMcpConfiguration.Load(path);
        Assert.True(first.StructureKey != first.FullKey, "Distinct credentials required.");
        Assert.True(!firstOutput.Contains(first.StructureKey) && !firstOutput.Contains(first.FullKey));
        await fixture.Host.DisposeAsync();
        await fixture.Host.StartAsync(first, fixture.Store, fixture.Store, fixture.Store, useEphemeralPort: true);
        var rotatedOutput = await Configure(fixture.DirectoryPath, "Rotate");
        var rotated = ScenarioMcpConfiguration.Load(path);
        Assert.True(!rotatedOutput.Contains(rotated.StructureKey) && !rotatedOutput.Contains(rotated.FullKey));
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", first.FullKey);
        using (var beforeRestart = await http.GetAsync(fixture.Host.Endpoint)) Assert.NotEqual(HttpStatusCode.Unauthorized, beforeRestart.StatusCode);
        await fixture.Host.DisposeAsync();
        await fixture.Host.StartAsync(rotated, fixture.Store, fixture.Store, fixture.Store, useEphemeralPort: true);
        using (var stale = await http.GetAsync(fixture.Host.Endpoint)) Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        await using (var client = await fixture.Client(rotated.FullKey)) Assert.NotEmpty(await client.ListToolsAsync());
        await Configure(fixture.DirectoryPath, "Disable");
        await fixture.Host.DisposeAsync();
        await fixture.Host.StartAsync(ScenarioMcpConfiguration.Load(path), fixture.Store, fixture.Store, fixture.Store, useEphemeralPort: true);
        Assert.Null(fixture.Host.Endpoint);
        Assert.Throws<InvalidOperationException>(() => new ScenarioMcpConfiguration { Enabled = true }.Validate());
        Assert.Throws<InvalidOperationException>(() => new ScenarioMcpConfiguration { Enabled = true, StructureKey = first.FullKey, FullKey = first.FullKey }.Validate());
        await File.WriteAllTextAsync(path, "{\"FullKey\":\"" + first.FullKey + "\",invalid}");
        var invalidConfig = Assert.Throws<InvalidOperationException>(() => ScenarioMcpConfiguration.Load(path));
        Assert.True(!invalidConfig.ToString().Contains(first.FullKey));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "公開schemaの整数IDは文字列で、2^53を超える返却ID/source/version/selectionを精度損失なく再投入できる"
    // oracle = { type = "contract", ref = "docs/design/scenario-mcp.md: 共通 snapshot と参照" }
    // fault = "long IDをJSON numberとして公開し丸める、返却wire参照を内部long DTOで要求する"
    // observable = "実tools/listのID schema型、9007199254740993の往復JSON値、範囲/分類選択と数値ID拒否"
    // observation_boundary = "consumer"
    // scope = "MCP 64-bit wire identifiers"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task WireIdentifiersAndSchemasRoundTripBeyondJsonIntegerPrecision()
    {
        await using var fixture = await Fixture.Create();
        await using var client = await fixture.Client(fixture.Keys.StructureKey);
        Assert.Equal(9007199254740993L, fixture.Game);
        Assert.Equal(9007199254740993L, fixture.Session);
        Assert.Equal(9007199254740993L, fixture.Range);
        foreach (var tool in await client.ListToolsAsync()) AssertIdentifierSchemas(tool.JsonSchema);
        var games = await Read(client, "scenario_games", new());
        var gameId = games[0].GetProperty("captureGameId");
        Assert.Equal(JsonValueKind.String, gameId.ValueKind);
        Assert.Equal("9007199254740993", gameId.GetString());
        var items = await fixture.Store.GetCaptureItemsAsync(fixture.Session);
        var label = (await fixture.Store.GetCaptureLabelsAsync(fixture.Game))[0];
        await fixture.Store.SetCaptureLabelsAsync(fixture.Session, [items.First(x => x.Sequence == 1).Id], label.Id);
        var snapshot = await Read(client, "scenario_snapshot", new() { ["gameId"] = gameId });
        var occurrence = snapshot.GetProperty("occurrences")[0];
        Assert.Equal("9007199254740993", occurrence.GetProperty("source").GetProperty("sessionId").GetString());
        Assert.Equal("9007199254740993", occurrence.GetProperty("labelId").GetString());
        var selected = await Read(client, "scenario_snapshot", new() { ["gameId"] = gameId,
            ["selection"] = new { rangeId = snapshot.GetProperty("ranges")[0].GetProperty("id"), labelId = occurrence.GetProperty("labelId") } });
        Assert.Single(selected.GetProperty("occurrences").EnumerateArray());
        var resolved = await Read(client, "scenario_by_source", new() { ["source"] = occurrence.GetProperty("source"), ["expectedVersion"] = snapshot.GetProperty("version") });
        Assert.Equal(occurrence.GetProperty("id").GetString(), resolved.GetProperty("item").GetProperty("id").GetString());
        Assert.True((await client.CallToolAsync("scenario_snapshot", new Dictionary<string, object?> { ["gameId"] = fixture.Game })).IsError);
        foreach (var invalid in new[] { "-1", "9223372036854775808", "not-an-id" })
            Assert.True((await client.CallToolAsync("scenario_snapshot", new Dictionary<string, object?> { ["gameId"] = invalid })).IsError);
    }

    private static void AssertIdentifierSchemas(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in properties.EnumerateObject())
                    if (property.Name is "gameId" or "captureGameId" or "sessionId" or "rangeId" or "labelId")
                    {
                        var type = property.Value.GetProperty("type");
                        var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString()).ToArray() : [type.GetString()];
                        Assert.Contains("string", types);
                        Assert.DoesNotContain("integer", types);
                        Assert.DoesNotContain("number", types);
                    }
            foreach (var property in schema.EnumerateObject()) AssertIdentifierSchemas(property.Value);
        }
        else if (schema.ValueKind == JsonValueKind.Array) foreach (var item in schema.EnumerateArray()) AssertIdentifierSchemas(item);
    }

    private static async Task<string> Configure(string directory, string action)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(AppContext.BaseDirectory, "configure-scenario-mcp.ps1"), "-DataDirectory", directory, "-Action", action }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        return await output + await error;
    }

    private sealed class ChangingKeyHandler(string key) : DelegatingHandler(new HttpClientHandler())
    {
        public string Key = key;
        public bool Replay;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new("Bearer", Key);
            if (Replay) { request.Headers.TryAddWithoutValidation("Mcp-Session-Id", "synthetic-previous-session"); request.Headers.TryAddWithoutValidation("Last-Event-ID", "synthetic-previous-event"); }
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static async Task<JsonElement> Read(McpClient client, string name, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(name, args);
        Assert.False(result.IsError == true, string.Join(";", result.Content.OfType<TextContentBlock>().Select(x => x.Text)));
        return JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement.Clone();
    }

    private static void AssertStructure(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name.ToLowerInvariant(), new[] { "text", "speaker", "excerpt", "preview", "thumbnail", "png", "blob", "filepath", "rawtext" });
                AssertStructure(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) AssertStructure(item);
        else if (value.ValueKind == JsonValueKind.String) Assert.DoesNotContain("SECRET", value.GetString());
    }

    // @test-value v2
    // kind = "contract"
    // claim = "実MCPの場面/tag直接ID・resource・JSON・ZIPは両キーで同ID/版/scopeを共有し、選択外/他作品/旧版を拒否、画像は新scopeとFull認可を維持する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動場面フロー; docs/design/scenario-mcp.md: 共通 snapshot と参照" }
    // fault = "sceneFlowだけ認可projectionを迂回する、64bit場面/tag IDを丸める、画像gateがSceneIds/TagIdsを比較しない、またはmetadata編集後も旧版を解決する"
    // observable = "公式SDKの場面/tag JSONとZIP、二権限のfield、コピーした画像要求のbytesとIsError、編集後resource拒否"
    // observation_boundary = "consumer"
    // scope = "manual scenes through real MCP and export authorization"
    // lifecycle = "permanent"
    // risk_tags = ["authorization", "privacy"]
    // @end-test-value
    [Fact]
    public async Task ManualScenesRoundTripAcrossMcpAndExportWithoutBypassingScopeOrTier()
    {
        await using var fixture = await Fixture.Create();
        var tag = await fixture.Store.GetOrCreateScenarioGameTagAsync(fixture.Game, "Explicit tag");
        var edit = new ScenarioSceneEdit("Authored scene", "01", "Chapter", 12, -34, [fixture.Range], [tag]);
        var scene = await fixture.Store.CreateScenarioSceneAsync(fixture.Game, edit);
        var empty = await fixture.Store.CreateScenarioSceneAsync(fixture.Game, new("Independent", "01", null, 0, 0, [], []));
        await fixture.Store.SetScenarioSceneConnectionAsync(fixture.Game, scene, empty, true);
        Assert.True(scene > 9007199254740992); Assert.True(tag > 9007199254740992);
        var sceneId = scene.ToString(CultureInfo.InvariantCulture); var tagId = tag.ToString(CultureInfo.InvariantCulture);
        await using var structure = await fixture.Client(fixture.Keys.StructureKey);
        await using var full = await fixture.Client(fixture.Keys.FullKey);
        var args = new Dictionary<string, object?> { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture) };
        var all = await Read(structure, "scenario_snapshot", args);
        var fullAll = await Read(full, "scenario_export_json", args);
        AssertStructure(all);
        Assert.Equal(all.GetProperty("sceneFlow").GetRawText(), fullAll.GetProperty("sceneFlow").GetRawText());
        Assert.Equal(all.GetProperty("version").GetRawText(), fullAll.GetProperty("version").GetRawText());
        Assert.Equal(all.GetProperty("selectionVersion").GetRawText(), fullAll.GetProperty("selectionVersion").GetRawText());
        var version = all.GetProperty("version").Deserialize<ScenarioMaterialWireVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var (collection, id) in new[] { ("scenes", sceneId), ("tags", tagId) })
        {
            var byId = new Dictionary<string, object?>(args) { ["collection"] = collection, ["id"] = id, ["expectedVersion"] = version, ["level"] = "Full" };
            var read = await Read(structure, "scenario_by_id", byId);
            AssertStructure(read); Assert.Equal(id, read.GetProperty("item").GetProperty("id").GetString());
            var uri = $"scenario://item/{fixture.Game}/{version.Revision}/{version.MatchingRevision}/{collection}/{Uri.EscapeDataString(id)}";
            var resource = await structure.ReadResourceAsync(uri);
            Assert.Equal(read.GetRawText(), Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text);
            Assert.True((await structure.CallToolAsync("scenario_by_id", new Dictionary<string, object?>(byId)
            { ["selection"] = new { includeTags = new[] { "absent" } } })).IsError);
            var other = await Read(structure, "scenario_snapshot", new() { ["gameId"] = fixture.OtherGame.ToString(CultureInfo.InvariantCulture) });
            Assert.True((await structure.CallToolAsync("scenario_by_id", new Dictionary<string, object?>(byId)
            { ["gameId"] = fixture.OtherGame.ToString(CultureInfo.InvariantCulture), ["expectedVersion"] = other.GetProperty("version") })).IsError);
        }
        foreach (var level in new[] { ScenarioMaterialAccessLevel.Structure, ScenarioMaterialAccessLevel.Full })
        {
            var path = Path.Combine(fixture.DirectoryPath, $"scene-{level}.zip");
            await fixture.Store.ExportScenarioArchiveAsync(fixture.Game, new(), path, level);
            using var zip = ZipFile.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(zip.GetEntry("material.json")!.Open());
            Assert.Equal(all.GetProperty("sceneFlow").GetRawText(), json.RootElement.GetProperty("sceneFlow").GetRawText());
            Assert.Equal(version, json.RootElement.GetProperty("version").Deserialize<ScenarioMaterialWireVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.True(all.GetProperty("selectionVersion").Deserialize<ScenarioMaterialSelectionVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!.SameSelection(
                json.RootElement.GetProperty("selectionVersion").Deserialize<ScenarioMaterialSelectionVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!));
        }
        var selection = new ScenarioMcpSelection(RangeId: fixture.Range.ToString(CultureInfo.InvariantCulture));
        var selected = await Read(full, "scenario_snapshot", new(args) { ["selection"] = selection });
        Assert.Equal(sceneId, Assert.Single(selected.GetProperty("sceneFlow").GetProperty("scenes").EnumerateArray()).GetProperty("id").GetString());
        Assert.Empty(selected.GetProperty("sceneFlow").GetProperty("connections").EnumerateArray());
        var scope = selected.GetProperty("selectionVersion").Deserialize<ScenarioMaterialSelectionVersion>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var source = Assert.Single(selected.GetProperty("ranges").EnumerateArray()).GetProperty("spans")[0];
        var imageArgs = new Dictionary<string, object?> { ["source"] = source, ["imageId"] = fixture.ImageId,
            ["expectedVersion"] = selected.GetProperty("version"), ["selectionVersion"] = scope, ["selection"] = selection };
        var image = await full.CallToolAsync("scenario_image_by_source", imageArgs);
        Assert.False(image.IsError == true);
        Assert.Equal(Fixture.Png, Assert.IsType<ImageContentBlock>(Assert.Single(image.Content)).DecodedData.ToArray());
        Assert.True((await structure.CallToolAsync("scenario_image_by_source", new Dictionary<string, object?>(imageArgs) { ["level"] = "Full" })).IsError);
        foreach (var forgedScope in new[] { scope with { SceneIds = [] }, scope with { TagIds = [] } })
            Assert.True((await full.CallToolAsync("scenario_image_by_source", new Dictionary<string, object?>(imageArgs) { ["selectionVersion"] = forgedScope })).IsError);
        var oldUri = $"scenario://item/{fixture.Game}/{version.Revision}/{version.MatchingRevision}/scenes/{Uri.EscapeDataString(sceneId)}";
        await fixture.Store.UpdateScenarioSceneAsync(fixture.Game, scene, edit with { Title = "Renamed", X = 100 });
        Assert.True((await full.CallToolAsync("scenario_image_by_source", imageArgs)).IsError);
        await Assert.ThrowsAsync<McpProtocolException>(async () => await structure.ReadResourceAsync(oldUri));
        Assert.True((await structure.CallToolAsync("scenario_by_id", new Dictionary<string, object?>(args)
        { ["collection"] = "scenes", ["id"] = sceneId, ["expectedVersion"] = version })).IsError);
        var changed = await Read(structure, "scenario_snapshot", args);
        Assert.Equal(sceneId, changed.GetProperty("sceneFlow").GetProperty("scenes")[0].GetProperty("id").GetString());
        Assert.Equal(version.MatchingRevision, changed.GetProperty("version").GetProperty("matchingRevision").GetInt64());
    }

    // @test-value v2
    // kind = "contract"
    // claim = "区間話者集計は共通reader/両ZIP material/公式SDKの両キーtool/resourceで同値となり、個別話者と検索/旧版認可は緩まない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 区間話者集計; docs/design/scenario-mcp.md: 認証と二権限; 共通 snapshot と参照" }
    // fault = "ZIP/MCPが別の集計を使う、区間外話者を含む、Structureが個別話者や話者検索/旧版参照を許す"
    // observable = "実HTTP SDKと両ZIPのrange JSON、64bit ID/版/scope、未確定件数、機密property不在とIsError"
    // observation_boundary = "consumer"
    // scope = "range speaker aggregation over real MCP and archive paths"
    // lifecycle = "permanent"
    // risk_tags = ["authorization", "privacy"]
    // @end-test-value
    [Fact]
    public async Task RangeSpeakerCountsShareReaderZipAndMcpPathsWithoutDisclosingIndividualSpeakers()
    {
        await using var fixture = await Fixture.Create();
        var items = await fixture.Store.GetCaptureItemsAsync(fixture.Session, 0);
        var label = (await fixture.Store.GetCaptureLabelsAsync(fixture.Game)).Single(x => x.Kind == CaptureKind.Dialogue);
        await fixture.Store.SetCaptureLabelsAsync(fixture.Session, items.Select(x => x.Id).ToArray(), label.Id);
        await fixture.Store.SetCaptureSpeakerAsync(fixture.Session, items[0].Id, "Range speaker");
        var unknownRange = await fixture.Store.SaveCaptureRangeAsync(fixture.Game, "Unknown speaker", new(fixture.Session, 2, 0),
            new(fixture.Session, 2, items[1].Length));
        await using var structure = await fixture.Client(fixture.Keys.StructureKey);
        await using var full = await fixture.Client(fixture.Keys.FullKey);
        var args = new Dictionary<string, object?> { ["gameId"] = fixture.Game.ToString(CultureInfo.InvariantCulture) };
        var all = await Read(structure, "scenario_snapshot", args);
        var version = all.GetProperty("version");
        var ranges = all.GetProperty("ranges");
        var known = ranges.EnumerateArray().Single(x => x.GetProperty("id").GetString() == fixture.Range.ToString(CultureInfo.InvariantCulture));
        var unknown = ranges.EnumerateArray().Single(x => x.GetProperty("id").GetString() == unknownRange.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("9007199254740993", known.GetProperty("id").GetString());
        var count = Assert.Single(known.GetProperty("speakerCounts").EnumerateArray());
        Assert.Equal("Range speaker", count.GetProperty("speakerName").GetString());
        Assert.Equal(1, count.GetProperty("dialogueCount").GetInt32());
        Assert.Equal(0, known.GetProperty("unknownSpeakerDialogueCount").GetInt32());
        Assert.Empty(unknown.GetProperty("speakerCounts").EnumerateArray());
        Assert.Equal(1, unknown.GetProperty("unknownSpeakerDialogueCount").GetInt32());
        AssertStructure(all);
        foreach (var (client, level) in new[] { (structure, ScenarioMaterialAccessLevel.Structure), (full, ScenarioMaterialAccessLevel.Full) })
        {
            var snapshot = await Read(client, "scenario_snapshot", args);
            var exported = await Read(client, "scenario_export_json", args);
            Assert.Equal(ranges.GetRawText(), snapshot.GetProperty("ranges").GetRawText());
            Assert.Equal(ranges.GetRawText(), exported.GetProperty("ranges").GetRawText());
            var byId = await Read(client, "scenario_by_id", new(args) { ["collection"] = "ranges", ["id"] = known.GetProperty("id"), ["expectedVersion"] = version });
            Assert.Equal(known.GetRawText(), byId.GetProperty("item").GetRawText());
            var uri = $"scenario://item/{fixture.Game}/{version.GetProperty("revision").GetInt64()}/{version.GetProperty("matchingRevision").GetInt64()}/ranges/{fixture.Range}";
            var resource = await client.ReadResourceAsync(uri);
            var resourceJson = JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(resource.Contents)).Text).RootElement;
            Assert.Equal(known.GetRawText(), resourceJson.GetProperty("item").GetRawText());
            var materialUri = $"scenario://material/{fixture.Game}/{version.GetProperty("revision").GetInt64()}/{version.GetProperty("matchingRevision").GetInt64()}";
            var materialResource = await client.ReadResourceAsync(materialUri);
            Assert.Equal(ranges.GetRawText(), JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(materialResource.Contents)).Text)
                .RootElement.GetProperty("ranges").GetRawText());
            var selected = await Read(client, "scenario_snapshot", new(args) { ["selection"] = new { rangeId = known.GetProperty("id") } });
            Assert.Equal(known.GetRawText(), Assert.Single(selected.GetProperty("ranges").EnumerateArray()).GetRawText());
            var reader = await fixture.Store.GetScenarioProjectionAsync(fixture.Game, new(), level);
            var readerJson = JsonSerializer.SerializeToElement(reader, reader.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(JsonSerializer.Serialize(ranges), JsonSerializer.Serialize(readerJson.GetProperty("ranges")));
            var path = Path.Combine(fixture.DirectoryPath, $"range-speakers-{level}.zip");
            await fixture.Store.ExportScenarioArchiveAsync(fixture.Game, new(), path, level);
            using var zip = ZipFile.OpenRead(path);
            using var zipped = await JsonDocument.ParseAsync(zip.GetEntry("material.json")!.Open());
            Assert.Equal(JsonSerializer.Serialize(ranges), JsonSerializer.Serialize(zipped.RootElement.GetProperty("ranges")));
            Assert.Equal(version.GetRawText(), zipped.RootElement.GetProperty("version").GetRawText());
            Assert.Equal(all.GetProperty("selectionVersion").GetRawText(), zipped.RootElement.GetProperty("selectionVersion").GetRawText());
            if (level == ScenarioMaterialAccessLevel.Structure)
            {
                AssertStructure(byId); AssertStructure(resourceJson); AssertStructure(exported); AssertStructure(zipped.RootElement);
            }
        }
        Assert.True((await structure.CallToolAsync("scenario_snapshot", new Dictionary<string, object?>(args)
            { ["selection"] = new { speakerSearch = "Range speaker" } })).IsError);
        var rawSource = all.GetProperty("occurrences")[0].GetProperty("source");
        AssertStructure(await Read(structure, "scenario_by_source", new() { ["source"] = rawSource, ["expectedVersion"] = version }));
        await fixture.Store.SetCaptureSpeakerAsync(fixture.Session, items[0].Id, null);
        Assert.True((await structure.CallToolAsync("scenario_by_id", new Dictionary<string, object?>(args)
            { ["collection"] = "ranges", ["id"] = known.GetProperty("id"), ["expectedVersion"] = version })).IsError);
        var changed = await Read(structure, "scenario_snapshot", args);
        var changedRange = changed.GetProperty("ranges")[0];
        Assert.Equal(known.GetProperty("id").GetString(), changedRange.GetProperty("id").GetString());
        Assert.Empty(changedRange.GetProperty("speakerCounts").EnumerateArray());
        Assert.Equal(1, changedRange.GetProperty("unknownSpeakerDialogueCount").GetInt32());
        Assert.True(changed.GetProperty("version").GetProperty("revision").GetInt64() > version.GetProperty("revision").GetInt64());
        Assert.Equal(all.GetProperty("selectionVersion").GetRawText(), changed.GetProperty("selectionVersion").GetRawText());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(), "mcp-" + Guid.NewGuid().ToString("N"));
        public readonly ScenarioMcpConfiguration Keys = new() { Enabled = true, StructureKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), FullKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
        public readonly ScenarioMcpHost Host = new();
        public SqliteGameLibraryStore Store = null!;
        public long Game, OtherGame, Session, Range;
        public string ImageId = "", OtherImageId = "", UnrelatedImageId = "";
        public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=");
        public static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.DirectoryPath);
            fixture.Store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(fixture.DirectoryPath, "synthetic.db"));
            await fixture.Store.GetCaptureProfilesAsync();
            await using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.DirectoryPath, "synthetic.db")}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO sqlite_sequence(name,seq) VALUES('CaptureGames',9007199254740992),('CaptureSessions',9007199254740992),('CaptureRanges',9007199254740992),('CaptureLabels',9007199254740992),('ScenarioScenes',9007199254740992),('ScenarioGameTags',9007199254740992)";
                await command.ExecuteNonQueryAsync();
            }
            var source = new CaptureSource(1, 2, 3, 0, "synthetic", "body");
            foreach (var gameId in new[] { "synthetic-a", "synthetic-b" })
            {
                await fixture.Store.UpsertAsync(new(gameId, gameId, "synthetic.exe", null, null, null, GameCardStatus.Unknown));
                var game = (await fixture.Store.GetOrCreateCaptureProfileAsync(gameId)).Id;
                var session = await fixture.Store.BeginCaptureSessionAsync(game);
                await fixture.Store.AppendCaptureAsync(session, new(1, DateTimeOffset.UtcNow, source, "SECRET_BODY_A", CaptureRole.Body), new());
                await fixture.Store.AppendCaptureAsync(session, new(2, DateTimeOffset.UtcNow, source, "SECRET_BODY_B", CaptureRole.Body), new());
                var image = await fixture.Store.SaveScreenshotAsync(new(game, new(session, 1)), new(1, 1, new string(gameId == "synthetic-a" ? 'a' : 'b', 64), Png), DateTimeOffset.UtcNow);
                await fixture.Store.EndCaptureSessionAsync(session);
                if (gameId == "synthetic-a") { fixture.Game = game; fixture.Session = session; fixture.ImageId = image.ImageId; }
                else { fixture.OtherGame = game; fixture.OtherImageId = image.ImageId; }
            }
            fixture.Range = await fixture.Store.SaveCaptureRangeAsync(fixture.Game, "Selected", new(fixture.Session, 1, 0), new(fixture.Session, 1, 13), ["selected"]);
            fixture.UnrelatedImageId = (await fixture.Store.SaveScreenshotAsync(new(fixture.Game, new(fixture.Session, 2)),
                new(1, 1, new string('c', 64), Png), DateTimeOffset.UtcNow)).ImageId;
            await fixture.Host.StartAsync(fixture.Keys, fixture.Store, fixture.Store, fixture.Store, useEphemeralPort: true);
            return fixture;
        }
        public Task<McpClient> Client(string key) => McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = Host.Endpoint!, TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key }
        }));
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Store.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(DirectoryPath, true);
        }
    }
}
