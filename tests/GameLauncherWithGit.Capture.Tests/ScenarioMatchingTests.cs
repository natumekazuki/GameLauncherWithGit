using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests
{

public sealed class ScenarioMatchingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(), "matching-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteGameLibraryStore _store;
    private static readonly CaptureSource Source = new(10, 100, 200, 0, "H", "body");
    private static readonly string[] Common = ["冒頭の短文", "長い会話のアンカーになる文章です", "続きの短文", "うん", "共通の後半へ戻る長い文章です", "次の場面", "結びの言葉"];
    public ScenarioMatchingTests()
    {
        Directory.CreateDirectory(_directory);
        _store = Open();
    }
    private SqliteGameLibraryStore Open() => new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
    private async Task<long> CreateAsync(string id = "game")
    {
        await _store.UpsertAsync(new(id, id, id + ".exe", null, null, null, GameCardStatus.Unknown));
        return (await _store.GetOrCreateCaptureProfileAsync(id)).Id;
    }
    private async Task<long> AddAsync(long game, IEnumerable<string> texts)
    {
        var session = await _store.BeginCaptureSessionAsync(game);
        long sequence = 0;
        foreach (var text in texts)
            await _store.AppendCaptureAsync(session, new(++sequence, DateTimeOffset.UtcNow, Source, text, CaptureRole.Body), new() { OutsideIsNarration = true });
        return session;
    }
    private static string[] Reconstruct(ScenarioMaterialSnapshot snapshot, long session) => snapshot.Occurrences
        .Where(x => x.Source.SessionId == session).Select(x => snapshot.Contents.Single(c => c.Id == x.ContentId).Text).ToArray();

    // @test-value v2
    // kind = "invariant"
    // claim = "タグなし3周の共通本文を一度だけ提供し分岐短文と再合流および各回の画像を復元できる"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "単語一致だけで分岐を消す、合流を逃す、または共通本文へ別周回の画像を混ぜる"
    // observable = "公開snapshotの共通本文数、回別復元本文、出現と画像対応、選択内の参照"
    // observation_boundary = "public-boundary"
    // scope = "persistent matching and selected material projection"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ThreeVisitsPreserveBranchRejoinAndVisitOnlyImage()
    {
        var game = await CreateAsync();
        var first = await AddAsync(game, Common);
        var variant = Common.Select(x => x == "うん" ? "いや" : x).ToArray();
        var second = await AddAsync(game, variant);
        var third = await AddAsync(game, Common);
        var image = await _store.SaveScreenshotAsync(new(game, new(second, 4)), new(1, 1, new string('a', 64), [1]), DateTimeOffset.UtcNow);
        var material = await _store.GetScenarioMaterialAsync(game, new());
        Assert.True(material.MatchingComplete);
        Assert.Equal(8, material.Contents.Count);
        Assert.Equal(Common, Reconstruct(material, first));
        Assert.Equal(variant, Reconstruct(material, second));
        Assert.Equal(Common, Reconstruct(material, third));
        Assert.Equal(image.ImageId, Assert.Single(Assert.Single(material.Occurrences, x => x.Images.Count > 0).Images).ImageId);
        Assert.Equal(second, Assert.Single(material.Occurrences, x => x.Images.Count > 0).Source.SessionId);
        Assert.Equal(21, (await _store.GetCaptureHistoryCountsAsync(game)).Events);
        await _store.SaveCaptureRangeAsync(game, "二周目", new(second, 1, 0), new(second, 7, Common[6].Length), ["second"]);
        var selected = await _store.GetScenarioMaterialAsync(game, new(IncludeTags: ["second"]));
        Assert.Equal(variant, Reconstruct(selected, second));
        Assert.All(selected.Occurrences, x => Assert.Equal(second, x.Source.SessionId));
        Assert.All(selected.Matches.SelectMany(x => x.Items), x => Assert.Equal(second, x.Representative.SessionId));
        Assert.DoesNotContain(selected.Contents, x => x.Text == "うん");
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "短文列だけと確定話者が異なる列は別場面のまま残る"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "短い定型句または異なる確定話者を共通本文へ確定する"
    // observable = "snapshotの共通本文数と各出現のcontent参照"
    // observation_boundary = "public-boundary"
    // scope = "sequence anchor and speaker verification"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ShortPhrasesAndConflictingSpeakersRemainSeparate()
    {
        var game = await CreateAsync();
        await AddAsync(game, ["はい", "うん", "そう"]);
        await AddAsync(game, ["はい", "うん", "そう"]);
        Assert.Equal(6, (await _store.GetScenarioMaterialAsync(game, new())).Contents.Count);
        var first = await AddAsync(game, Common.Take(3));
        var second = await AddAsync(game, Common.Take(3));
        var label = (await _store.GetCaptureLabelsAsync(game)).Single(x => x.Kind == CaptureKind.Dialogue).Id;
        foreach (var (session, speaker) in new[] { (first, "A"), (second, "B") })
        {
            var items = await _store.GetCaptureItemsAsync(session, 0);
            await _store.SetCaptureLabelsAsync(session, items.Select(x => x.Id).ToArray(), label);
            foreach (var item in items) await _store.SetCaptureSpeakerAsync(session, item.Id, speaker);
        }
        var material = await _store.GetScenarioMaterialAsync(game, new());
        Assert.Equal(12, material.Contents.Count);
        Assert.Empty(material.Matches);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "別場面に同じ3連続がある間は保留し次の本文で一意になった候補だけ遡及確定する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "最初の候補を即採用して曖昧な場面を統合する、または候補の絞込み後に先頭を復元しない"
    // observable = "3件時と4件時の公開snapshotのcontent参照"
    // observation_boundary = "public-boundary"
    // scope = "ambiguous candidate continuation"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task AmbiguousSceneWaitsForDisambiguatingContinuation()
    {
        var game = await CreateAsync();
        var first = await AddAsync(game, Common.Take(3).Append("別場面A"));
        var second = await AddAsync(game, Common.Take(3).Append("別場面B"));
        // Give the second scene a conflicting known speaker on its continuation, preventing prior equivalence.
        // The identical prefix is ambiguous only if it was not previously established as the same scene.
        var dialogue = (await _store.GetCaptureLabelsAsync(game)).Single(x => x.Kind == CaptureKind.Dialogue).Id;
        foreach (var session in new[] { first, second })
        {
            var items = await _store.GetCaptureItemsAsync(session, 0);
            await _store.SetCaptureLabelsAsync(session, items.Select(x => x.Id).ToArray(), dialogue);
            await _store.SetCaptureSpeakerAsync(session, items[0].Id, session == first ? "A" : "B");
        }
        var third = await AddAsync(game, Common.Take(3));
        var thirdItems = await _store.GetCaptureItemsAsync(third, 0);
        await _store.SetCaptureLabelsAsync(third, thirdItems.Select(x => x.Id).ToArray(), dialogue);
        var pending = await _store.GetScenarioMaterialAsync(game, new());
        Assert.DoesNotContain(pending.Matches.SelectMany(x => x.Items), x => x.Revisit.SessionId == third);
        await _store.AppendCaptureAsync(third, new(4, DateTimeOffset.UtcNow, Source, "別場面B", CaptureRole.Body, dialogue), new());
        var material = await _store.GetScenarioMaterialAsync(game, new());
        var target = material.Occurrences.Where(x => x.Source.SessionId == third).ToArray();
        Assert.Equal(material.Occurrences.Single(x => x.Source.SessionId == second && x.Source.ReceiveSequence == 1).ContentId, target[0].ContentId);
        Assert.NotEqual(material.Occurrences.Single(x => x.Source.SessionId == first && x.Source.ReceiveSequence == 1).ContentId, target[0].ContentId);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "取得回末尾と別取得回先頭を繋いだ一致は作らず同一取得回の後の非重複再訪は照合する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "session境界を跨ぐseedまたは自身を候補とする、同session再訪を全て除外する"
    // observable = "snapshotのContents数と同session再訪の対応参照"
    // observation_boundary = "public-boundary"
    // scope = "capture session and self matching boundaries"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task SessionBoundaryIsNotASeedButSameSessionRevisitIs()
    {
        var game = await CreateAsync();
        await AddAsync(game, Common.Take(2));
        await AddAsync(game, Common.Skip(2).Take(1));
        await AddAsync(game, Common.Take(3));
        Assert.Equal(6, (await _store.GetScenarioMaterialAsync(game, new())).Contents.Count);
        var other = await CreateAsync("other");
        var sameSession = await AddAsync(other, Common.Take(3).Concat(["異なる場面です"]).Concat(Common.Take(3)));
        var material = await _store.GetScenarioMaterialAsync(other, new());
        Assert.Equal(4, material.Contents.Count);
        Assert.Equal(3, material.Matches.Sum(x => x.Items.Count));
        Assert.All(material.Matches.SelectMany(x => x.Items), x => Assert.Equal(sameSession, x.Revisit.SessionId));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "短batchの中断後に永続進捗から再開し追加本文を処理し変更なし再読では再照合しない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "進捗がmemoryだけに残る、追加分を逃す、または再出力で全件の照合世代を進める"
    // observable = "batch処理件数、再開snapshotとMatchingRevision、追加後の本文"
    // observation_boundary = "public-boundary"
    // scope = "persistent batch cursor and material version"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task BatchResumeAppendAndNoChangeReadUsePersistentProgress()
    {
        var game = await CreateAsync();
        await AddAsync(game, Common);
        var second = await AddAsync(game, Common);
        var batch = await _store.ProcessScenarioMatchingBatchAsync(game, 2);
        Assert.Equal(2, batch.ProcessedItems); Assert.True(batch.HasMore);
        using var reopened = Open();
        var material = await reopened.GetScenarioMaterialAsync(game, new());
        Assert.Equal(7, material.Contents.Count);
        Assert.Equal(material.Version, (await reopened.GetScenarioMaterialAsync(game, new())).Version);
        Assert.Equal(0, (await reopened.ProcessScenarioMatchingBatchAsync(game)).ProcessedItems);
        await reopened.AppendCaptureAsync(second, new(8, DateTimeOffset.UtcNow, Source, "追加された長い本文です", CaptureRole.Body), new() { OutsideIsNarration = true });
        var appended = await reopened.GetScenarioMaterialAsync(game, new());
        Assert.True(appended.Version.Revision > material.Version.Revision);
        Assert.True(appended.Version.MatchingRevision > material.Version.MatchingRevision);
        Assert.Equal("追加された長い本文です", Reconstruct(appended, second)[^1]);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "代表側と再訪側のそれぞれで確定済み一致の分類・話者訂正・再分割・削除後に旧対応を使わない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "訂正前のcanonical対応を残し、失効した位置や別分類を共通本文に混ぜる"
    // observable = "各操作直前の確定対応、操作後の両世代・対応集合・分類/話者・位置と回別原文復元"
    // observation_boundary = "public-boundary"
    // scope = "matching invalidation and rebuild"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditsOnEitherSideInvalidateOldMatches(bool editRepresentative)
    {
        string[] lines = ["「同一の最初の長い発言です」", "「一致する二番目の発言です」", "「共通の三番目の発言です」"];
        foreach (var operation in new[] { "label", "speaker", "split", "delete" })
        {
            // Each mutation starts from its own confirmed correspondence, not a prior invalidation.
            var game = await CreateAsync($"edit-{editRepresentative}-{operation}");
            var first = await AddAsync(game, lines);
            var second = await AddAsync(game, lines);
            foreach (var session in new[] { first, second })
                foreach (var item in await _store.GetCaptureItemsAsync(session, 0))
                    await _store.SetCaptureSpeakerAsync(session, item.Id, "話者A");
            var before = await _store.GetScenarioMaterialAsync(game, new());
            Assert.True(before.MatchingComplete);
            Assert.Equal(3, before.Contents.Count);
            var oldPairs = before.Matches.SelectMany(x => x.Items).ToArray();
            Assert.Equal(3, oldPairs.Length);
            Assert.All(oldPairs, pair =>
            {
                Assert.Equal(first, pair.Representative.SessionId);
                Assert.Equal(second, pair.Revisit.SessionId);
            });
            Assert.All(before.Occurrences, x => Assert.Equal("話者A", x.Speaker));
            var edited = editRepresentative ? first : second;
            var other = editRepresentative ? second : first;
            var firstItem = (await _store.GetCaptureItemsAsync(edited, 0))[0];
            switch (operation)
            {
                case "label": await _store.SetCaptureLabelsAsync(edited, [firstItem.Id], null); break;
                case "speaker": await _store.SetCaptureSpeakerAsync(edited, firstItem.Id, "話者B"); break;
                case "split": await _store.SplitCaptureEventAsync(edited, 2, [5]); break;
                case "delete": await _store.DeleteCaptureEventsAsync(game, [new(edited, 3)]); break;
            }
            var after = await _store.GetScenarioMaterialAsync(game, new());
            Assert.True(after.MatchingComplete);
            Assert.True(after.Version.Revision > before.Version.Revision);
            Assert.True(after.Version.MatchingRevision > before.Version.MatchingRevision);
            Assert.Empty(after.Matches);
            Assert.Equal(operation == "split" ? 7 : operation == "delete" ? 5 : 6, after.Contents.Count);
            Assert.Equal(after.Occurrences.Count, after.Occurrences.Select(x => x.ContentId).Distinct().Count());
            string[] ReconstructEvents(long session) => after.Occurrences.Where(x => x.Source.SessionId == session)
                .GroupBy(x => x.Source.ReceiveSequence).Select(group => string.Concat(group.Select(x =>
                    after.Contents.Single(c => c.Id == x.ContentId).Text))).ToArray();
            Assert.Equal(lines, ReconstructEvents(other));
            Assert.Equal(operation == "delete" ? lines.Take(2) : lines, ReconstructEvents(edited));
            var changed = after.Occurrences.Where(x => x.Source.SessionId == edited).ToArray();
            switch (operation)
            {
                case "label": Assert.Null(changed[0].LabelId); Assert.Null(after.Contents.Single(x => x.Id == changed[0].ContentId).Kind); break;
                case "speaker": Assert.Equal("話者B", changed[0].Speaker); break;
                case "split":
                    Assert.Equal(new[] { new ScenarioOccurrenceRef(game, edited, 2, 0, 5), new(game, edited, 2, 5, lines[1].Length - 5) },
                        changed.Where(x => x.Source.ReceiveSequence == 2).Select(x => x.Source));
                    Assert.DoesNotContain(changed, x => x.Source == new ScenarioOccurrenceRef(game, edited, 2, 0, lines[1].Length));
                    break;
                case "delete": Assert.DoesNotContain(changed, x => x.Source.ReceiveSequence == 3); break;
            }
        }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "部分選択の共通本文と画像・話者・出現参照は選択内だけから構成される"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "選択外の代表本文または話者や画像を展開する、切取offsetを共通本文へ誤適用する"
    // observable = "選択snapshotの本文、speaker、画像集合、元UTF16範囲"
    // observation_boundary = "public-boundary"
    // scope = "selection before common material projection"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task PartialSelectionDoesNotExposeHistoricalRepresentative()
    {
        var game = await CreateAsync();
        var first = await AddAsync(game, Common.Take(3));
        var second = await AddAsync(game, Common.Take(3));
        var dialogue = (await _store.GetCaptureLabelsAsync(game)).Single(x => x.Kind == CaptureKind.Dialogue).Id;
        foreach (var session in new[] { first, second })
        {
            var items = await _store.GetCaptureItemsAsync(session, 0);
            await _store.SetCaptureLabelsAsync(session, items.Select(x => x.Id).ToArray(), dialogue);
        }
        await _store.SetCaptureSpeakerAsync(first, (await _store.GetCaptureItemsAsync(first, 0))[1].Id, "選択外の話者");
        await _store.SaveScreenshotAsync(new(game, new(first, 2)), new(1, 1, new string('b', 64), [1]), DateTimeOffset.UtcNow);
        await _store.GetScenarioMaterialAsync(game, new());
        var range = await _store.SaveCaptureRangeAsync(game, "部分", new(second, 2, 2), new(second, 2, 8));
        var material = await _store.GetScenarioMaterialAsync(game, new(RangeId: range));
        Assert.Equal(Common[1].Substring(2, 6), Assert.Single(material.Contents).Text);
        var occurrence = Assert.Single(material.Occurrences);
        Assert.Equal(new ScenarioOccurrenceRef(game, second, 2, 2, 6), occurrence.Source);
        Assert.Null(occurrence.Speaker); Assert.Empty(occurrence.Images); Assert.Empty(material.Matches);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "アプリworkerは未処理と追加分を処理し、停止joinは受理済み本文の保存を妨げず次の起動で再開できる"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "起動scan後のAppendでworkerを再通知しない、取消で進捗を壊す、または終了TaskとDB処理を切離す"
    // observable = "同workerでreader前に増える追加索引件数と版、再起動後の本文数、Stop後の保存、Dispose後の排他DBアクセス"
    // observation_boundary = "public-boundary"
    // scope = "application matching worker lifetime and persisted resume"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ApplicationWorkerResumesAndJoinsBeforeDatabaseRelease()
    {
        var game = await CreateAsync();
        var lines = Enumerable.Range(0, 120).Select(x => $"連続照合の合成本文{x:D3}は長い文章です").ToArray();
        await AddAsync(game, lines);
        await _store.ProcessScenarioMatchingBatchAsync(game, 2);
        await _store.DisposeAsync();
        using (var firstWorker = Open())
        {
            firstWorker.StartScenarioMatching();
            await WaitForIndexAsync(120);
            var version = await firstWorker.GetScenarioMaterialVersionAsync(game);
            Assert.Equal(0, (await firstWorker.ProcessScenarioMatchingBatchAsync(game)).ProcessedItems);
            Assert.Equal(version, await firstWorker.GetScenarioMaterialVersionAsync(game));
            async Task AppendVisitAsync()
            {
                var session = await firstWorker.BeginCaptureSessionAsync(game);
                for (var i = 0; i < lines.Length; i++)
                    await firstWorker.AppendCaptureAsync(session, new(i + 1, DateTimeOffset.UtcNow, Source, lines[i], CaptureRole.Body), new() { OutsideIsNarration = true });
            }
            await AppendVisitAsync();
            // Only Append may wake the now-idle worker: no material read, manual batch or restart.
            await WaitForIndexAsync(240);
            var liveVersion = await firstWorker.GetScenarioMaterialVersionAsync(game);
            Assert.True(liveVersion.Revision > version.Revision);
            Assert.True(liveVersion.MatchingRevision > version.MatchingRevision);
            var live = await firstWorker.GetScenarioMaterialAsync(game, new());
            Assert.Equal(liveVersion, live.Version);
            Assert.True(live.MatchingComplete); Assert.Equal(120, live.Contents.Count); Assert.Equal(240, live.Occurrences.Count);
            await AppendVisitAsync();
            // Cancel even if a short batch is still pending; Dispose joins, rather than abandoning it.
            await firstWorker.DisposeAsync();
        }
        using (var nextWorker = Open())
        {
            nextWorker.StartScenarioMatching();
            await WaitForIndexAsync(360);
            var beforeRead = await nextWorker.GetScenarioMaterialVersionAsync(game);
            var material = await nextWorker.GetScenarioMaterialAsync(game, new());
            Assert.Equal(beforeRead, material.Version);
            Assert.True(material.MatchingComplete); Assert.Equal(120, material.Contents.Count); Assert.Equal(360, material.Occurrences.Count);
            await nextWorker.DisposeAsync();
        }
        using (var stoppedWorker = Open())
        {
            stoppedWorker.StartScenarioMatching();
            await stoppedWorker.StopScenarioMatchingAsync();
            var session = await stoppedWorker.BeginCaptureSessionAsync(game);
            Assert.True(await stoppedWorker.AppendCaptureAsync(session,
                new(1, DateTimeOffset.UtcNow, Source, "終了時に既に受理されていた本文", CaptureRole.Body), new() { OutsideIsNarration = true }));
            Assert.Equal("終了時に既に受理されていた本文", Assert.Single(await stoppedWorker.GetCaptureItemsAsync(session)).Text);
        }
        SqliteConnection.ClearAllPools();
        using var exclusive = new FileStream(Path.Combine(_directory, "library.db"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "長文anchor確定時はanchorの前の短文まで遡り、分岐直後の本文を次の探索に使う"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "seed以前の共通短文を落とす、または分岐を探索から捨てて直後の合流を逃す"
    // observable = "遡及した先頭のcontent参照と分岐後の共通本文数、回別復元列"
    // observation_boundary = "public-boundary"
    // scope = "retroactive anchor matching and branch restart"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task AnchorBacktracksAndMismatchBecomesTheNextSeed()
    {
        var game = await CreateAsync();
        string[] firstLines = ["短文一", "短文二", "短文三", "ここで長いanchorとなる文章です", "基準分岐", "合流の長い先頭文章です", "合流二", "合流三"];
        var first = await AddAsync(game, firstLines);
        var secondLines = firstLines.Select(x => x == "基準分岐" ? "別分岐" : x).ToArray();
        var second = await AddAsync(game, secondLines);
        var material = await _store.GetScenarioMaterialAsync(game, new());
        Assert.Equal(9, material.Contents.Count);
        Assert.Equal(firstLines, Reconstruct(material, first));
        Assert.Equal(secondLines, Reconstruct(material, second));
        Assert.Equal(material.Occurrences.First(x => x.Source.SessionId == first).ContentId,
            material.Occurrences.First(x => x.Source.SessionId == second).ContentId);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "未確定話者の代表を介しても異なる確定話者どうしを同一場面へ統合しない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "未確定を同一性の追加証拠にしてAとBを間接的に同じcanonicalへ結ぶ"
    // observable = "三回のcontent参照と各回の有効話者"
    // observation_boundary = "public-boundary"
    // scope = "confirmed speaker consistency across established visits"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task UnknownSpeakerCannotBridgeTwoConflictingKnownSpeakers()
    {
        var game = await CreateAsync();
        var unknown = await AddAsync(game, Common.Take(3));
        var a = await AddAsync(game, Common.Take(3));
        var b = await AddAsync(game, Common.Take(3));
        var label = (await _store.GetCaptureLabelsAsync(game)).Single(x => x.Kind == CaptureKind.Dialogue).Id;
        foreach (var session in new[] { unknown, a, b })
        {
            var items = await _store.GetCaptureItemsAsync(session, 0);
            await _store.SetCaptureLabelsAsync(session, items.Select(x => x.Id).ToArray(), label);
            if (session != unknown)
                foreach (var item in items) await _store.SetCaptureSpeakerAsync(session, item.Id, session == a ? "A" : "B");
        }
        var material = await _store.GetScenarioMaterialAsync(game, new());
        Assert.Equal(6, material.Contents.Count);
        var first = material.Occurrences.First(x => x.Source.SessionId == unknown);
        Assert.Equal(first.ContentId, material.Occurrences.First(x => x.Source.SessionId == a).ContentId);
        Assert.NotEqual(first.ContentId, material.Occurrences.First(x => x.Source.SessionId == b).ContentId);
        Assert.Null(first.Speaker);
        Assert.All(material.Occurrences.Where(x => x.Source.SessionId == a), x => Assert.Equal("A", x.Speaker));
        Assert.All(material.Occurrences.Where(x => x.Source.SessionId == b), x => Assert.Equal("B", x.Speaker));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "照合未処理snapshotでも同一原文の異なる等長選択sliceを別の本文として出力する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#連続本文照合と共通資料" }
    // fault = "未処理itemの相対offsetを全て0にして別sliceの本文を先のsliceへ置換する"
    // observable = "未準備の共通snapshotのmatchingComplete、本文ABとEF、元offset0と4"
    // observation_boundary = "component-behavior"
    // scope = "ZIP/shared snapshot projection while capture defers matching"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task UnprocessedSnapshotKeepsEqualLengthSlicesDistinct()
    {
        var game = await CreateAsync();
        var session = await AddAsync(game, ["ABCDEFGHIJKL"]);
        await _store.SaveCaptureRangeAsync(game, "先", new(session, 1, 0), new(session, 1, 2), ["selected"]);
        await _store.SaveCaptureRangeAsync(game, "後", new(session, 1, 4), new(session, 1, 6), ["selected"]);
        var material = await _store.ReadUnpreparedMaterialForTestAsync(game, new(IncludeTags: ["selected"]));
        Assert.False(material.MatchingComplete);
        Assert.Equal(new[] { "AB", "EF" }, material.Contents.Select(x => x.Text));
        Assert.Equal(new[] { 0, 4 }, material.Occurrences.Select(x => x.Source.StartUtf16));
        Assert.NotEqual(material.Occurrences[0].ContentId, material.Occurrences[1].ContentId);
    }

    private async Task WaitForIndexAsync(int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}");
            await connection.OpenAsync(timeout.Token);
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM ScenarioSequenceIndex";
            if (Convert.ToInt32(await query.ExecuteScalarAsync(timeout.Token)) == expected) return;
            await Task.Delay(10, timeout.Token);
        }
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
}

namespace GameLauncherWithGit.Infrastructure.Services
{
    // Compiled only in this source-linked test assembly. Exercise the exact snapshot entry point
    // used by the ZIP writer, without forcing preparation that would hide its pending/dirty branch.
    public sealed partial class SqliteGameLibraryStore
    {
        internal async Task<ScenarioMaterialSnapshot> ReadUnpreparedMaterialForTestAsync(long game, ScenarioArchiveSelection selection)
        {
            await EnsureInitializedAsync(default);
            await using var connection = await OpenConnectionAsync(default);
            await using var transaction = connection.BeginTransaction(deferred: true);
            var ranges = await ReadCaptureRangesAsync(connection, transaction, game);
            var items = await ReadCaptureExportItemsAsync(connection, transaction, game, selection, ranges);
            return await ReadScenarioMaterialAsync(connection, transaction, game, selection, ranges, items);
        }
    }
}
