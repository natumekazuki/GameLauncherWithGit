using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioFlowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(), "flow-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteGameLibraryStore _store;
    private static readonly CaptureSource Source = new(1, 1, 1, 1, "body", "body");
    public ScenarioFlowTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "synthetic.db"));
    }
    private async Task<(long Game, long Session)> BeginAsync()
    {
        await _store.UpsertAsync(new("synthetic", "合成シナリオ", "", null, null, null, GameCardStatus.Unknown));
        var profile = await _store.GetOrCreateCaptureProfileAsync("synthetic");
        return (profile.Id, await _store.BeginCaptureSessionAsync(profile.Id));
    }
    private Task<bool> AppendAsync(long session, long sequence, string text, bool cut = false) =>
        _store.AppendCaptureAsync(session, new(sequence, DateTimeOffset.UtcNow, Source, text, CaptureRole.Body), new() { SplitDialogue = false }, cut);
    private static ScenarioFlowPart Part(long session, long sequence, string text, string? block = null) =>
        new(block ?? $"{session}:{sequence}", new(session, sequence, 0), new(session, sequence, text.Length), text, null);
    private static ScenarioFlowGraph Build(IEnumerable<ScenarioFlowPart> parts, ScenarioFlowOverrides? overrides = null) =>
        ScenarioFlowBuilder.Build(parts.ToArray(), overrides ?? new([], []));

    // @test-value v2
    // kind = "invariant"
    // claim = "タグも区間もない既存の1件記録は再読込後も単独チャートに所属し原文から参照できる"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/34" }
    // fault = "初期タグや辺を所属条件にして1件履歴を孤立した未所属にする"
    // observable = "再オープンしたSQLiteの本文とチャート所属、空の辺と手動区間"
    // observation_boundary = "public-boundary"
    // scope = "untagged persisted singleton flow"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ExistingUntaggedSingletonHasAChartWithoutAnEdge()
    {
        var (game, session) = await BeginAsync();
        await AppendAsync(session, 1, "一件だけの本文");
        await _store.EndCaptureSessionAsync(session);
        var reopened = new SqliteGameLibraryStore(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "synthetic.db"));
        var item = Assert.Single(await reopened.GetGameCaptureItemsAsync(game, 0));
        var graph = Build([Part(item.SessionId, item.Sequence, item.Text)], await reopened.GetScenarioFlowOverridesAsync(game));
        Assert.Equal(Assert.Single(graph.Charts).Id, Assert.Single(graph.Occurrences).ChartId);
        Assert.Equal(item.StartPosition, graph.Occurrences[0].Start);
        Assert.Empty(graph.Edges);
        Assert.Empty(await reopened.GetCaptureRangesAsync(game));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "同じ接続を複数周回で観測した後に一出現の直前を切っても他周回の辺と後続接続は残る"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/34" }
    // fault = "共通blockの辺自体を削除するか区切り後の辺も失う"
    // observable = "AからBとBからCの辺に付く周回別traversalと全出現の所属"
    // observation_boundary = "public-boundary"
    // scope = "occurrence-local path break"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task CuttingOneVisitPreservesOtherVisitsAndItsOutgoingEdge()
    {
        var (game, first) = await BeginAsync();
        var (_, second) = await BeginAsync();
        foreach (var session in new[] { first, second })
            for (var sequence = 1; sequence <= 3; sequence++) await AppendAsync(session, sequence, ((char)('A' + sequence - 1)).ToString());
        await _store.SetScenarioFlowCutAsync(game, new(second, 2, 0));
        var graph = Build(new[] { first, second }.SelectMany(session => new[] { Part(session, 1, "A", "A"), Part(session, 2, "B", "B"), Part(session, 3, "C", "C") }), await _store.GetScenarioFlowOverridesAsync(game));
        Assert.Equal(3, graph.Blocks.Count);
        var ab = Assert.Single(graph.Edges, e => graph.Blocks.Single(b => b.Id == e.FromBlockId).Text == "A");
        Assert.Equal(first, Assert.Single(ab.Traversals).SessionId);
        var bc = Assert.Single(graph.Edges, e => graph.Blocks.Single(b => b.Id == e.FromBlockId).Text == "B");
        Assert.Equal(new[] { first, second }, bc.Traversals.Select(x => x.SessionId));
        Assert.All(graph.Occurrences, x => Assert.False(string.IsNullOrEmpty(x.ChartId)));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "出現内のcutは再分割後も原文位置に残り、対象原文削除後は次の出現へ移らない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/34" }
    // fault = "CaptureItemsの一時IDでcutを保存し再分割で失うか削除後の次項目へ付け直す"
    // observable = "保存したcutのraw位置、共有blockの分割と周回別辺、削除後のCutBefore"
    // observation_boundary = "public-boundary"
    // scope = "raw cut persistence across repartition and deletion"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task CutKeepsItsRawPositionThroughResplittingAndDeletion()
    {
        var (game, first) = await BeginAsync();
        var (_, second) = await BeginAsync();
        await AppendAsync(first, 1, "AB"); await AppendAsync(second, 1, "AB"); await AppendAsync(second, 2, "C");
        await _store.SplitCaptureEventAsync(second, 1, [1]);
        await _store.SetScenarioFlowCutAsync(game, new(second, 1, 1));
        await _store.SplitCaptureEventAsync(second, 1, []);
        var overrides = await _store.GetScenarioFlowOverridesAsync(game);
        Assert.Equal(new CapturePosition(second, 1, 1), Assert.Single(overrides.Cuts));
        var graph = Build([Part(first, 1, "AB", "AB"), Part(second, 1, "AB", "AB")], overrides);
        Assert.Equal(2, graph.Blocks.Count);
        Assert.Equal(first, Assert.Single(Assert.Single(graph.Edges).Traversals).SessionId);
        Assert.True(graph.Occurrences.Single(x => x.Start == new CapturePosition(second, 1, 1)).CutBefore);
        await _store.DeleteCaptureEventsAsync(game, [new(second, 1)]);
        graph = Build([Part(second, 2, "C")], await _store.GetScenarioFlowOverridesAsync(game));
        Assert.False(Assert.Single(graph.Occurrences).CutBefore);
        Assert.Single(graph.Charts);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "回想を独立所属にすると本文・画像と内部順序を残し、本編の前後を接続せず、回想全削除後にも架空の辺を作らない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/34" }
    // fault = "回想をskipして本編前後を直結するか所属変更で本文参照を落とす"
    // observable = "保存範囲の独立chart、内部traversal、出現参照と元イベントの保存画像ID、削除後の空の辺"
    // observation_boundary = "public-boundary"
    // scope = "independent recollection flow ownership"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task IndependentRecollectionNeverBridgesTheSurroundingStory()
    {
        var (game, session) = await BeginAsync();
        for (var i = 1; i <= 4; i++) await AppendAsync(session, i, $"本文{i}");
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 2)), new(1, 1, new string('b', 64), [1]), DateTimeOffset.UtcNow);
        await _store.CreateIndependentScenarioFlowAsync(game, new(session, 2, 0), new(session, 3, 3));
        var overrides = await _store.GetScenarioFlowOverridesAsync(game);
        var graph = Build(Enumerable.Range(1, 4).Select(i => Part(session, i, $"本文{i}")), overrides);
        Assert.Equal(3, graph.Charts.Count);
        Assert.Equal(graph.Occurrences[1].ChartId, graph.Occurrences[2].ChartId);
        Assert.NotEqual(graph.Occurrences[0].ChartId, graph.Occurrences[1].ChartId);
        var edge = Assert.Single(graph.Edges);
        Assert.Equal(graph.Occurrences[1].Id, Assert.Single(edge.Traversals).FromOccurrenceId);
        Assert.Equal(graph.Occurrences[2].Id, edge.Traversals[0].ToOccurrenceId);
        Assert.Equal(image.ImageId, Assert.Single(await _store.GetEventScreenshotsAsync(game, new(session, 2))).ImageId);
        await _store.DeleteCaptureEventsAsync(game, [new(session, 2), new(session, 3)]);
        graph = Build([Part(session, 1, "本文1"), Part(session, 4, "本文4")], await _store.GetScenarioFlowOverridesAsync(game));
        Assert.Empty(graph.Edges);
        Assert.Equal(2, graph.Charts.Count);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "共通readerの確認済み出現対応だけで分岐合流と同周回の再訪をまとめ、同文だけの未確認出現は統合しない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/34" }
    // fault = "同文だけで未確認出現を統合する、同じ枝を増殖させる、または再訪を失う"
    // observable = "adapter出力の共通block数、枝の数と周回別traversal、再訪Aへの戻り辺、未確認Aの別block"
    // observation_boundary = "component-behavior"
    // scope = "material match graph adapter"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public void ConfirmedMatchesMergeBranchesAndRevisitsWithoutMergingUnconfirmedText()
    {
        var visits = new[] { (1L, new[] { "A", "B", "C" }), (2L, new[] { "A", "D", "C" }), (3L, new[] { "A", "B", "A", "C" }), (4L, new[] { "A" }) };
        var occurrences = visits.SelectMany(v => v.Item2.Select((text, index) => new ScenarioOccurrence($"{v.Item1}:{index}", new(1, v.Item1, index + 1, 0, 1), text, null, null, null, DateTimeOffset.UnixEpoch, []))).ToArray();
        ScenarioOccurrenceRef Ref(long s, long q) => new(1, s, q, 0, 1);
        var pairs = new[] { new ScenarioOccurrencePair(Ref(1, 1), Ref(2, 1)), new(Ref(1, 1), Ref(3, 1)), new(Ref(1, 1), Ref(3, 3)), new(Ref(1, 2), Ref(3, 2)), new(Ref(1, 3), Ref(2, 3)), new(Ref(1, 3), Ref(3, 4)) };
        var material = new ScenarioMaterialSnapshot(new(1, 1, 1), true, new[] { "A", "B", "C", "D" }.Select(x => new ScenarioContent(x, x, null)).ToArray(), occurrences, [new("confirmed", pairs)]);
        var graph = ScenarioFlowMaterialAdapter.Build(material, new([], []));
        Assert.Equal(5, graph.Blocks.Count);
        Assert.Equal(6, graph.Edges.Count);
        var a = graph.Occurrences.Single(x => x.Start == new CapturePosition(1, 1, 0)).BlockId;
        var b = graph.Occurrences.Single(x => x.Start == new CapturePosition(1, 2, 0)).BlockId;
        Assert.Equal(2, graph.Edges.Single(x => x.FromBlockId == a && x.ToBlockId == b).Traversals.Count);
        Assert.Contains(graph.Edges, x => x.FromBlockId == b && x.ToBlockId == a);
        Assert.NotEqual(a, graph.Occurrences.Single(x => x.Start.SessionId == 4).BlockId);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "実共通readerの未タグ3周を分岐合流の8blockへまとめ、回別cutとその回だけの画像参照を維持する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/34" }
    // fault = "readerとadapterのraw参照を取り違え、分岐を増殖させるか別周回へcutや画像を付ける"
    // observable = "実SQLiteからのgraphのblock/edge数、cut後の周回別traversal、画像付き出現のraw参照"
    // observation_boundary = "public-boundary"
    // scope = "material reader store graph integration"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task RealMaterialReaderConnectsUntaggedBranchesToVisitOnlyImages()
    {
        var (game, first) = await BeginAsync();
        var (_, second) = await BeginAsync(); var (_, third) = await BeginAsync();
        string[] opening = ["放課後の教室には夕焼けが差し込んでいた。", "窓辺に立つ彼女を見つめていた。", "言いかけた言葉を飲み込んだ。"];
        string[] ending = ["翌朝の登校。いつもの通学路を歩く。", "校門の前に友人の姿を見つけた。", "教室の扉を開けて挨拶を交わした。"];
        foreach (var session in new[] { first, second, third })
        {
            long sequence = 0;
            foreach (var text in opening.Append(session == second ? "教室に残る" : "美咲を追う").Concat(ending))
                await _store.AppendCaptureAsync(session, new(++sequence, DateTimeOffset.UtcNow, Source, text, CaptureRole.Body), new() { OutsideIsNarration = true });
            await _store.EndCaptureSessionAsync(session);
        }
        var image = await _store.SaveScreenshotAsync(new(game, new(second, 4)), new(1, 1, new string('a', 64), [1]), DateTimeOffset.UtcNow);
        var material = await _store.GetScenarioMaterialAsync(game, new());
        var graph = ScenarioFlowMaterialAdapter.Build(material, await _store.GetScenarioFlowOverridesAsync(game));
        Assert.True(material.MatchingComplete);
        Assert.Single(graph.Charts); Assert.Equal(8, graph.Blocks.Count); Assert.Equal(8, graph.Edges.Count);
        var branch = graph.Occurrences.Single(x => x.Start == new CapturePosition(second, 4, 0));
        var source = Assert.Single(material.Occurrences, x => x.Images.Count > 0);
        Assert.Equal(branch.Start.SessionId, source.Source.SessionId); Assert.Equal(branch.Start.Sequence, source.Source.ReceiveSequence);
        Assert.Equal(image.ImageId, Assert.Single(source.Images).ImageId);
        await _store.SetScenarioFlowCutAsync(game, branch.Start);
        graph = ScenarioFlowMaterialAdapter.Build(await _store.GetScenarioMaterialAsync(game, new()), await _store.GetScenarioFlowOverridesAsync(game));
        Assert.DoesNotContain(graph.Edges.SelectMany(x => x.Traversals), x => x.ToOccurrenceId == branch.Id);
        Assert.Contains(graph.Edges.SelectMany(x => x.Traversals), x => x.FromOccurrenceId == branch.Id);
        Assert.Contains(graph.Edges.SelectMany(x => x.Traversals), x => x.SessionId == first && x.ToOccurrenceId == ScenarioFlowBuilder.PositionId(new(first, 4, 0)));
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
