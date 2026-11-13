using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioScreenshotStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(), "screenshots-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteGameLibraryStore _store;
    private static readonly CaptureSource Source = new(1, 2, 3, 0, "hook", "body");
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-11-06T12:00:00+09:00");
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=");
    private static ScreenshotImageData Image(char hash = 'a') => new(1, 1, new string(hash, 64), Png);
    public ScenarioScreenshotStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
    }
    private async Task<(long Game, long Session)> StartAsync(string game = "game")
    {
        await _store.UpsertAsync(new(game, game, "game.exe", null, null, null, GameCardStatus.Unknown));
        var profile = await _store.GetOrCreateCaptureProfileAsync(game);
        return (profile.Id, await _store.BeginCaptureSessionAsync(profile.Id));
    }
    private Task<bool> Append(long session, long sequence, string text = "本文", CaptureRole role = CaptureRole.Body) =>
        _store.AppendCaptureAsync(session, new(sequence, Time.AddSeconds(-sequence), Source, text, role), new());
    private string Output(string name) => Path.Combine(_directory, name + ".zip");
    private static async Task<JsonElement[]> Rows(ZipArchive zip)
    {
        using var reader = new StreamReader(zip.GetEntry("texts.jsonl")!.Open());
        var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Select(x => JsonDocument.Parse(x).RootElement.Clone()).ToArray();
    }

    private static IEnumerable<string> JsonStrings(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().SelectMany(x => JsonStrings(x.Value)),
        JsonValueKind.Array => value.EnumerateArray().SelectMany(JsonStrings),
        JsonValueKind.String => [value.GetString()!],
        _ => []
    };

    // @test-value v2
    // kind = "invariant"
    // claim = "同一画素はイベント間で共有し同一添付の再撮影は最初の時刻を保持し再分割後も関連が残る"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "重複画像を増やす、再撮影で時刻を更新する、またはitem ID変更で添付を失う"
    // observable = "公開読取の画像IDと撮影時刻、PNG件数、再分割後の添付"
    // observation_boundary = "public-boundary"
    // scope = "SQLite screenshot deduplication and stable event binding"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task DeduplicatesPixelsAndPreservesLinksAcrossRepartition()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "前本文後");
        await Append(session, 2);
        var first = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        var repeat = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time.AddMinutes(1));
        var second = await _store.SaveScreenshotAsync(new(game, new(session, 2)), Image(), Time.AddMinutes(2));
        await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image('b'), Time.AddMinutes(3));
        await _store.SplitCaptureEventAsync(session, 1, [1, 3]);
        Assert.Equal(first.ImageId, repeat.ImageId);
        Assert.Equal(Time, repeat.CapturedAt);
        Assert.Equal(first.ImageId, second.ImageId);
        Assert.Equal(Time.AddMinutes(2), second.CapturedAt);
        Assert.Equal(2, (await _store.GetEventScreenshotsAsync(game, new(session, 1))).Count);
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(first.FilePath)!, "*.png").Length);
        await _store.DeleteAsync("game");
        Assert.Equal(first.ImageId, Assert.Single(await _store.GetEventScreenshotsAsync(game, new(session, 2))).ImageId);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "未保存・名前・空本文・別ゲームのイベントへの撮影保存を拒否し既存共有画像を損なわない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "所有者やBody存在を検証せず添付を作る、または失敗rollbackで既存画像を削除する"
    // observable = "保存例外、既存添付とPNG内容、一時ファイルの不在"
    // observation_boundary = "public-boundary"
    // scope = "screenshot save validation and failure ownership"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task RejectsInvalidTargetsWithoutDamagingExistingImage()
    {
        var (game, session) = await StartAsync();
        var (other, _) = await StartAsync("other");
        await Append(session, 1);
        await Append(session, 2, "名前", CaptureRole.Name);
        await Append(session, 3, "  ");
        var saved = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        foreach (var target in new ScreenshotTarget[] { new(game, new(session, 99)), new(game, new(session, 2)), new(game, new(session, 3)), new(other, new(session, 1)) })
            await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveScreenshotAsync(target, Image(), Time));
        Assert.Equal(Png, await File.ReadAllBytesAsync(saved.FilePath));
        Assert.Single(await _store.GetEventScreenshotsAsync(game, new(session, 1)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(saved.FilePath)!, "*.tmp"));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "イベント削除と全履歴消去は他ゲームから共有される画像を保持し最後の参照だけを回収する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "一ゲーム削除で共有PNGを消す、または最終参照削除後も画像metadataと実体を残す"
    // observable = "他ゲーム添付とPNG存在、最終消去後のPNG件数と再保存画像ID"
    // observation_boundary = "public-boundary"
    // scope = "screenshot garbage collection on explicit history deletion"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task DeletionReclaimsOnlyImagesLosingTheirLastReference()
    {
        var (game, session) = await StartAsync();
        var (other, otherSession) = await StartAsync("other");
        await Append(session, 1);
        await Append(otherSession, 1);
        var shared = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        var privateImage = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image('b'), Time);
        await _store.SaveScreenshotAsync(new(other, new(otherSession, 1)), Image(), Time);
        Assert.Equal(0, (await _store.DeleteCaptureEventsAsync(game, [new(session, 1)])).ScreenshotCleanup.RemainingImageCount);
        Assert.True(File.Exists(shared.FilePath));
        Assert.False(File.Exists(privateImage.FilePath));
        Assert.Single(await _store.GetEventScreenshotsAsync(other, new(otherSession, 1)));
        Assert.Equal(0, (await _store.ClearCaptureHistoryAsync(other)).ScreenshotCleanup.RemainingImageCount);
        Assert.False(File.Exists(shared.FilePath));
        await Append(session, 2);
        var fresh = await _store.SaveScreenshotAsync(new(game, new(session, 2)), Image(), Time);
        Assert.NotEqual(shared.ImageId, fresh.ImageId);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "PNG削除失敗でも本文削除は確定し未回収画像を再撮影で共有でき明示再試行で回収できる"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/25" }
    // fault = "画像削除失敗を本文削除失敗にする、metadataを先に消す、または再利用中PNGを回収する"
    // observable = "本文件数、削除結果の残件と理由、再撮影画像ID、retry後のPNG存在"
    // observation_boundary = "public-boundary"
    // scope = "committed history deletion and retryable image cleanup"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCleanupKeepsCommittedDeletionAndSupportsReuseAndRetry(bool clearHistory)
    {
        var (game, session) = await StartAsync();
        var (other, otherSession) = await StartAsync("other");
        await Append(session, 1);
        await Append(otherSession, 1);
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        using (var locked = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = clearHistory
                ? await _store.ClearCaptureHistoryAsync(game)
                : await _store.DeleteCaptureEventsAsync(game, [new(session, 1)]);
            Assert.Empty(await _store.GetGameCaptureItemsAsync(game));
            Assert.Equal(1, result.ScreenshotCleanup.RemainingImageCount);
            Assert.NotEmpty(result.ScreenshotCleanup.FailureReasons);
            Assert.Equal(1, (await _store.CleanupUnreferencedScreenshotsAsync()).RemainingImageCount);
            var reused = await _store.SaveScreenshotAsync(new(other, new(otherSession, 1)), Image(), Time);
            Assert.Equal(image.ImageId, reused.ImageId);
        }
        Assert.Equal(0, (await _store.CleanupUnreferencedScreenshotsAsync()).RemainingImageCount);
        Assert.True(File.Exists(image.FilePath));
        Assert.Single(await _store.GetEventScreenshotsAsync(other, new(otherSession, 1)));
        using (var locked = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(1, (await _store.ClearCaptureHistoryAsync(other)).ScreenshotCleanup.RemainingImageCount);
        var cleanup = await _store.CleanupUnreferencedScreenshotsAsync();
        Assert.Equal(0, cleanup.RemainingImageCount);
        Assert.Empty(cleanup.FailureReasons);
        Assert.False(File.Exists(image.FilePath));
        Assert.Equal(0, (await _store.CleanupUnreferencedScreenshotsAsync()).RemainingImageCount);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "本文削除transactionの途中で対象不一致またはDB失敗となった場合は本文と画像をすべて保持する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#保存と登録削除" }
    // fault = "DB削除失敗前の本文やPNGを確定削除してしまう"
    // observable = "削除例外、本文件数、添付とPNGの内容"
    // observation_boundary = "public-boundary"
    // scope = "capture deletion transaction rollback"
    // lifecycle = "permanent"
    // @end-test-value
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDeletionRollsBackBeforeImageCleanup(bool databaseFailure)
    {
        var (game, session) = await StartAsync();
        await Append(session, 1);
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        if (databaseFailure)
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_event_delete BEFORE DELETE ON CaptureEvents BEGIN SELECT RAISE(ABORT,'injected delete failure'); END";
            await command.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<SqliteException>(() => _store.DeleteCaptureEventsAsync(game, [new(session, 1)]));
        }
        else
            await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteCaptureEventsAsync(game, [new(session, 1), new(session, 99)]));
        Assert.Single(await _store.GetGameCaptureItemsAsync(game));
        Assert.Single(await _store.GetEventScreenshotsAsync(game, new(session, 1)));
        Assert.Equal(Png, await File.ReadAllBytesAsync(image.FilePath));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "表示ページの原文キー集合に対し重複をまとめ画像枚数とゼロ件を返し別所有者は拒否する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/28" }
    // fault = "同じ原文を重複加算する、未添付行を落とす、または別ゲームの画像件数を返す"
    // observable = "200原文の件数辞書と所有者不一致の例外"
    // observation_boundary = "public-boundary"
    // scope = "batched screenshot counts for visible history"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task CountsVisibleEventImagesIncludingZeroAndRejectsOtherOwner()
    {
        var (game, session) = await StartAsync();
        var (other, otherSession) = await StartAsync("other");
        for (var i = 1; i <= 200; i++) await Append(session, i);
        await Append(otherSession, 1);
        await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image('b'), Time);
        await _store.SaveScreenshotAsync(new(game, new(session, 2)), Image(), Time);
        await _store.SaveScreenshotAsync(new(other, new(otherSession, 1)), Image('c'), Time);
        var keys = Enumerable.Range(1, 200).Select(x => new CaptureEventKey(session, x)).Append(new(session, 1)).ToArray();
        var counts = await _store.GetScreenshotCountsAsync(game, keys);
        Assert.Equal(200, counts.Count);
        Assert.Equal(2, counts[new(session, 1)]);
        Assert.Equal(1, counts[new(session, 2)]);
        Assert.All(counts.Where(x => x.Key.Sequence > 2), x => Assert.Equal(0, x.Value));
        Assert.Empty(await _store.GetScreenshotCountsAsync(game, []));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetScreenshotCountsAsync(game, [new(session, 1), new(otherSession, 1)]));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "中止済みの撮影保存は既存本文を保ち画像ファイルと関連を作らない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/27" }
    // fault = "保存gateへの中止tokenを無視して終了中の撮影を後から保存する"
    // observable = "中止例外、保持された本文、添付とPNGの件数"
    // observation_boundary = "public-boundary"
    // scope = "cancelled screenshot persistence"
    // lifecycle = "permanent"
    // impact = "終了時の未保存結果と実保存データが食い違うのを防ぐ"
    // distinction = "serviceのfake中止では実ストアのtoken配線とDB・ファイル結果を検証できない"
    // @end-test-value
    [Fact]
    public async Task CancelledSaveDoesNotCreateAnImageOrLink()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time, cancelled.Token));
        Assert.Single(await _store.GetGameCaptureItemsAsync(game));
        Assert.Empty(await _store.GetEventScreenshotsAsync(game, new(session, 1)));
        Assert.Empty(Directory.GetFiles(_directory, "*.png", SearchOption.AllDirectories));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ZIPは区間で切った分類と名前と本文のAND一致segmentだけを元の位置と有効値で出し共有画像を一度だけ収録する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "未選択原文をrawText等で漏らす、検索をORにする、または添付の時刻や相対参照を失う"
    // observable = "ZIPのJSONLsegmentとmanifestと画像entry、全JSON内の除外文字列"
    // observation_boundary = "consumer"
    // scope = "self-contained filtered scenario archive"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ExportClipsAndFiltersWithoutLeakingExcludedRawText()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "秘密前ABC秘密後");
        await Append(session, 2, "ABC本文のみ");
        await Append(session, 3, "名前のみ");
        await Append(session, 4, "ABC");
        var labels = await _store.GetCaptureLabelsAsync(game);
        var dialogue = labels.Single(x => x.Kind == CaptureKind.Dialogue);
        var candidates = (await _store.GetCaptureItemsAsync(session)).Where(x => x.Sequence <= 3).ToArray();
        await _store.SetCaptureLabelsAsync(session, candidates.Select(x => x.Id).ToArray(), dialogue.Id);
        foreach (var candidate in candidates)
            await _store.SetCaptureSpeakerAsync(session, candidate.Id, candidate.Sequence == 2 ? "雪" : "花");
        var range = await _store.SaveCaptureRangeAsync(game, "選択", new(session, 1, 3), new(session, 4, 3));
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        await _store.SaveScreenshotAsync(new(game, new(session, 2)), Image('b'), Time);
        var output = Output("clipped");
        await _store.ExportScenarioArchiveAsync(game, new(range, Kind: CaptureKind.Dialogue, TextSearch: "abc", SpeakerSearch: "花"), output);
        using var zip = ZipFile.OpenRead(output);
        var row = Assert.Single(await Rows(zip));
        Assert.Equal(1, row.GetProperty("receiveSequence").GetInt64());
        var part = Assert.Single(row.GetProperty("segments").EnumerateArray());
        Assert.Equal("ABC秘密後", part.GetProperty("text").GetString());
        Assert.Equal(3, part.GetProperty("startUtf16").GetInt32());
        Assert.Equal("Dialogue", part.GetProperty("kind").GetString());
        Assert.Equal("花", part.GetProperty("speaker").GetString());
        Assert.Equal(dialogue.Name, part.GetProperty("label").GetProperty("name").GetString());
        Assert.DoesNotContain(JsonStrings(row), x => x.Contains("秘密前", StringComparison.Ordinal));
        Assert.DoesNotContain(JsonStrings(row), x => x.Contains("本文のみ", StringComparison.Ordinal));
        Assert.DoesNotContain(JsonStrings(row), x => x.Contains("名前のみ", StringComparison.Ordinal));
        Assert.False(row.TryGetProperty("rawText", out _));
        Assert.Equal(Time, row.GetProperty("images")[0].GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Equal(image.ImageId, row.GetProperty("images")[0].GetProperty("imageId").GetString());
        Assert.Single(zip.Entries, x => x.FullName.StartsWith("images/"));
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var info = Assert.Single(manifest.RootElement.GetProperty("images").EnumerateArray());
        Assert.NotNull(zip.GetEntry(info.GetProperty("path").GetString()!));
        Assert.Equal("GameLauncherWithGit.scenario", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(2, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "出力は表示200件を超えた全イベントを受信時刻でなくキー順に出し同一画像を一度だけ持つ"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "UIページ上限を適用する、時刻順に変更する、またはイベントごとに画像を複製する"
    // observable = "JSONL全件数と連番、ZIP画像entry件数"
    // observation_boundary = "consumer"
    // scope = "unpaged archive export ordering and deduplication"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ExportIncludesAllMatchesInStableOrderWithUniqueImages()
    {
        var (game, session) = await StartAsync();
        for (var i = 1; i <= 205; i++) await Append(session, i, "一致");
        await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        await _store.SaveScreenshotAsync(new(game, new(session, 205)), Image(), Time.AddSeconds(1));
        var output = Output("all");
        await _store.ExportScenarioArchiveAsync(game, new(TextSearch: "一致"), output);
        using var zip = ZipFile.OpenRead(output);
        var rows = await Rows(zip);
        Assert.Equal(Enumerable.Range(1, 205).Select(x => (long)x), rows.Select(x => x.GetProperty("receiveSequence").GetInt64()));
        Assert.Single(zip.Entries, x => x.FullName.StartsWith("images/"));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "一括分類変更と同時出力しても全segmentの有効分類は同一DBsnapshotから取得される"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "出力を個別ページの独立読取に分けて分類変更の前後を混在させる"
    // observable = "並行する一括変更とZIP出力の完了、JSONL全segmentの分類集合と件数"
    // observation_boundary = "consumer"
    // scope = "archive snapshot during concurrent text editing"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ConcurrentClassificationChangeExportsOneCoherentSnapshot()
    {
        var (game, session) = await StartAsync();
        for (var i = 1; i <= 210; i++) await Append(session, i);
        var labels = await _store.GetCaptureLabelsAsync(game);
        var dialogue = labels.Single(x => x.Kind == CaptureKind.Dialogue);
        var narration = labels.Single(x => x.Kind == CaptureKind.Narration);
        var ids = (await _store.GetGameCaptureItemsAsync(game, limit: 0)).Select(x => x.Id).ToArray();
        await _store.SetCaptureLabelsAsync(session, ids, dialogue.Id);
        using var start = new ManualResetEventSlim();
        var output = Output("snapshot");
        var change = Task.Run(async () => { start.Wait(); await _store.SetCaptureLabelsAsync(session, ids, narration.Id); });
        var export = Task.Run(async () => { start.Wait(); await _store.ExportScenarioArchiveAsync(game, new(), output); });
        start.Set();
        await Task.WhenAll(change, export);
        using var zip = ZipFile.OpenRead(output);
        var rows = await Rows(zip);
        Assert.Equal(210, rows.Length);
        var kinds = rows.SelectMany(x => x.GetProperty("segments").EnumerateArray()).Select(x => x.GetProperty("kind").GetString()).Distinct().ToArray();
        Assert.Single(kinds);
        Assert.Contains(kinds[0], new[] { "Dialogue", "Narration" });
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "既存PNG欠落は再撮影とZIP出力を失敗にし不完全なZIPや既存出力の上書きを残さない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/22" }
    // fault = "欠落ファイル参照を成功として返す、失敗ZIPを公開する、または既存ZIPを上書きする"
    // observable = "保存と出力の例外、出力未作成、一時ZIPの不在、既存ZIPのバイト列"
    // observation_boundary = "consumer"
    // scope = "screenshot and archive failure publication"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task MissingImageAndOutputFailureNeverPublishPartialArchive()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1);
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        var output = Output("existing");
        await _store.ExportScenarioArchiveAsync(game, new(), output);
        var original = await File.ReadAllBytesAsync(output);
        await Assert.ThrowsAsync<IOException>(() => _store.ExportScenarioArchiveAsync(game, new(), output));
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        var lockedOutput = Output("locked");
        using (var locked = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => _store.ExportScenarioArchiveAsync(game, new(), lockedOutput));
        Assert.False(File.Exists(lockedOutput));
        File.Delete(image.FilePath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time));
        var missingOutput = Output("missing");
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.ExportScenarioArchiveAsync(game, new(), missingOutput));
        Assert.False(File.Exists(missingOutput));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "区間タグは自由な単一行を正規化して複数保持し更新は名前とタグを同時に変更しゲーム間を越えない"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#区間タグと出力条件" }
    // fault = "重複タグや空タグを保存する、更新失敗で名前だけ変える、他ゲームのタグを読み書きする、削除後にタグを残す"
    // observable = "公開取得した区間名とタグ、入力例外、削除後のタグ行数と他ゲームの区間"
    // observation_boundary = "public-boundary"
    // scope = "range tag persistence and ownership"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task RangeTagsPersistUpdateAtomicallyAndFollowRangeLifetime()
    {
        var (game, session) = await StartAsync();
        var (other, otherSession) = await StartAsync("other");
        await Append(session, 1, "本文");
        await Append(otherSession, 1, "別本文");
        var id = await _store.SaveCaptureRangeAsync(game, "区間", new(session, 1, 0), new(session, 1, 2), [" 重要 ", "重要", "Tag", "tag"]);
        var otherId = await _store.SaveCaptureRangeAsync(other, "別区間", new(otherSession, 1, 0), new(otherSession, 1, 3), ["重要"]);
        Assert.Equal(new[] { "Tag", "tag", "重要" }, Assert.Single(await _store.GetCaptureRangesAsync(game)).Tags);
        var reopened = new SqliteGameLibraryStore(NullLogger<SqliteGameLibraryStore>.Instance, Path.Combine(_directory, "library.db"));
        Assert.Equal(new[] { "Tag", "tag", "重要" }, Assert.Single(await reopened.GetCaptureRangesAsync(game)).Tags);
        foreach (var invalid in new[] { " ", "a\nb", "a\rb" })
            await Assert.ThrowsAsync<ArgumentException>(() => _store.UpdateCaptureRangeAsync(game, id, "変更", [invalid]));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.UpdateCaptureRangeAsync(game, otherId, "変更", ["別"]));
        Assert.Equal("区間", Assert.Single(await _store.GetCaptureRangesAsync(game)).Name);
        await _store.UpdateCaptureRangeAsync(game, id, "変更", ["再利用", "重要"]);
        var updated = Assert.Single(await _store.GetCaptureRangesAsync(game));
        Assert.Equal("変更", updated.Name);
        Assert.Equal(new[] { "再利用", "重要" }, updated.Tags);
        await using (var failingConnection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}"))
        {
            await failingConnection.OpenAsync();
            await using var trigger = failingConnection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER fail_tag BEFORE INSERT ON CaptureRangeTags WHEN NEW.Tag='失敗' BEGIN SELECT RAISE(ABORT,'tag failure'); END";
            await trigger.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<SqliteException>(() => _store.UpdateCaptureRangeAsync(game, id, "不成立", ["失敗"]));
        }
        var retained = Assert.Single(await _store.GetCaptureRangesAsync(game));
        Assert.Equal(updated.Name, retained.Name);
        Assert.Equal(updated.Tags, retained.Tags);
        await _store.UpdateCaptureRangeAsync(game, id, "タグなし", []);
        Assert.Empty(Assert.Single(await _store.GetCaptureRangesAsync(game)).Tags);
        await _store.UpdateCaptureRangeAsync(game, id, "削除", ["重要"]);
        await _store.DeleteCaptureRangeAsync(game, id);
        Assert.Empty(await _store.GetCaptureRangesAsync(game));
        Assert.Equal(new[] { "重要" }, Assert.Single(await _store.GetCaptureRangesAsync(other)).Tags);
        await _store.ClearCaptureHistoryAsync(other);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}");
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM CaptureRangeTags";
        Assert.Equal(0L, await query.ExecuteScalarAsync());
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "タグ包含の和集合から除外を引いた原文位置を重複なく出力し区間・検索は切取後の本文へ適用する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#区間タグと出力条件" }
    // fault = "重複区間を二重出力する、部分除外で全itemを落とす、除外文字で検索一致する、再分割でタグ位置がずれる"
    // observable = "公開export取得の本文・UTF-16位置とTXT全文、交差条件による空結果"
    // observation_boundary = "public-boundary"
    // scope = "tag interval algebra and post-slice search"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ExportTagIntervalsMergeSubtractAndSearchAfterClipping()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "abcdefghij");
        var second = await _store.BeginCaptureSessionAsync(game);
        await Append(second, 1, "klmnop");
        await _store.SaveCaptureRangeAsync(game, "一", new(session, 1, 1), new(session, 1, 8), ["A"]);
        await _store.SaveCaptureRangeAsync(game, "重複", new(session, 1, 5), new(second, 1, 4), ["B"]);
        await _store.SaveCaptureRangeAsync(game, "除外", new(session, 1, 3), new(session, 1, 6), ["X", "A"]);
        await _store.SaveCaptureRangeAsync(game, "後除外", new(second, 1, 1), new(second, 1, 3), ["Y"]);
        var selection = new ScenarioArchiveSelection(IncludeTags: ["A", "B"], ExcludeTags: ["X", "Y"]);
        var items = await _store.GetCaptureExportItemsAsync(game, selection);
        Assert.Equal(new[] { "bc", "ghij", "k", "n" }, items.Select(x => x.Text));
        Assert.Equal(new[] { 1, 6, 0, 3 }, items.Select(x => x.Start));
        var txt = GameLauncherWithGit.Application.Services.ScenarioTextProcessor.FormatTxt(items).ReplaceLineEndings("\n");
        Assert.Equal("[未判定]\nbc\n\n[未判定]\nghij\n\n[未判定]\nk\n\n[未判定]\nn\n", txt);
        Assert.Empty(await _store.GetCaptureExportItemsAsync(game, selection with { TextSearch = "def" }));
        Assert.Empty(await _store.GetCaptureExportItemsAsync(game, selection with { TextSearch = "bcg" }));
        Assert.Equal("ghij", Assert.Single(await _store.GetCaptureExportItemsAsync(game, selection with { TextSearch = "GHI" })).Text);
        var baseRange = await _store.SaveCaptureRangeAsync(game, "狭い", new(session, 1, 2), new(session, 1, 7));
        Assert.Equal(new[] { "c", "g" }, (await _store.GetCaptureExportItemsAsync(game, selection with { RangeId = baseRange })).Select(x => x.Text));
        Assert.Equal(new[] { "abc", "ghij", "k", "nop" }, (await _store.GetCaptureExportItemsAsync(game, new(ExcludeTags: ["X", "Y"]))).Select(x => x.Text));
        await _store.SplitCaptureEventAsync(session, 1, [2, 7]);
        Assert.Equal("bcghijkn", string.Concat((await _store.GetCaptureExportItemsAsync(game, selection)).Select(x => x.Text)));
        Assert.Empty(await _store.GetCaptureExportItemsAsync(game, selection with { IncludeTags = ["missing"] }));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "画像付きZIPはTXTと同じタグ切取本文と残ったイベントの画像だけを収録しタグ条件を記録する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md#区間タグと出力条件" }
    // fault = "ZIPだけタグ条件を無視する、除外専用イベントの画像や原文を収録する、条件をmanifestから欠落させる"
    // observable = "実ZIPのtexts.jsonl本文とmanifest条件と画像entry集合、空選択ZIP"
    // observation_boundary = "consumer"
    // scope = "tag-filtered archive artifacts"
    // lifecycle = "permanent"
    // impact = "除外指定した本文や画像の意図しない持出しを防ぐ"
    // distinction = "型やTXT取得testでは実ZIPの収録画像・JSON漏出を確認できない"
    // @end-test-value
    [Fact]
    public async Task ArchiveUsesTagSlicesAndOnlyRemainingEventImages()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "前秘密後");
        await Append(session, 2, "全秘密");
        var kept = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        var hidden = await _store.SaveScreenshotAsync(new(game, new(session, 2)), Image('b'), Time);
        await _store.SaveCaptureRangeAsync(game, "対象", new(session, 1, 0), new(session, 2, 3), ["出力"]);
        await _store.SaveCaptureRangeAsync(game, "部分", new(session, 1, 1), new(session, 1, 3), ["除外"]);
        await _store.SaveCaptureRangeAsync(game, "全体", new(session, 2, 0), new(session, 2, 3), ["除外"]);
        var selection = new ScenarioArchiveSelection(IncludeTags: ["出力"], ExcludeTags: ["除外"]);
        await _store.ExportScenarioArchiveAsync(game, selection, Output("tags"));
        using (var zip = ZipFile.OpenRead(Output("tags")))
        {
            var row = Assert.Single(await Rows(zip));
            Assert.Equal(new[] { "前", "後" }, row.GetProperty("segments").EnumerateArray().Select(x => x.GetProperty("text").GetString()));
            Assert.False(row.TryGetProperty("rawText", out _));
            Assert.NotNull(zip.GetEntry($"images/{kept.ImageId}.png"));
            Assert.Null(zip.GetEntry($"images/{hidden.ImageId}.png"));
            using var manifest = await JsonDocument.ParseAsync(zip.GetEntry("manifest.json")!.Open());
            Assert.Equal("出力", manifest.RootElement.GetProperty("selection").GetProperty("includeTags")[0].GetString());
            Assert.Equal("除外", manifest.RootElement.GetProperty("selection").GetProperty("excludeTags")[0].GetString());
        }
        await _store.ExportScenarioArchiveAsync(game, selection with { IncludeTags = ["なし"] }, Output("empty-tags"));
        using var empty = ZipFile.OpenRead(Output("empty-tags"));
        Assert.Empty(await Rows(empty));
        Assert.DoesNotContain(empty.Entries, x => x.FullName.StartsWith("images/", StringComparison.Ordinal));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "無絞込みZIPは同名・重複・隣接・空の全保存区間を元境界とタグ付きで保持し取得回を越えて本文へ対応する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/30" }
    // fault = "区間を名前で統合する、UTF-16境界をずらす、空区間を落とす、または重複区間で本文を増やす"
    // observable = "実ZIPの区間ID・境界・タグ・交差spansとJSONL本文・イベント順"
    // observation_boundary = "consumer"
    // scope = "whole-game archive range definitions and source coordinates"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task WholeArchivePreservesAllRangesAndTheirExactTextIntersections()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "前😀ABC後");
        await Append(session, 2, "削除");
        var second = await _store.BeginCaptureSessionAsync(game);
        await Append(second, 1, "次本文");
        await Append(second, 2, "区間外");
        var first = await _store.SaveCaptureRangeAsync(game, "同名", new(session, 1, 1), new(session, 1, 5), ["章", "人物"]);
        var overlap = await _store.SaveCaptureRangeAsync(game, "同名", new(session, 1, 3), new(second, 1, 2), ["節"]);
        var adjacent = await _store.SaveCaptureRangeAsync(game, "隣接", new(session, 1, 5), new(session, 1, 7));
        var empty = await _store.SaveCaptureRangeAsync(game, "空", new(session, 2, 0), new(session, 2, 2), ["残す"]);
        await _store.DeleteCaptureEventsAsync(game, [new(session, 2)]);
        var (other, otherSession) = await StartAsync("other");
        await Append(otherSession, 1);
        await _store.SaveCaptureRangeAsync(other, "別ゲーム", new(otherSession, 1, 0), new(otherSession, 1, 2));
        await _store.ExportScenarioArchiveAsync(game, new(), Output("ranges"));
        using var zip = ZipFile.OpenRead(Output("ranges"));
        var rows = await Rows(zip);
        Assert.Equal(new[] { "前😀ABC後", "次本文", "区間外" }, rows.SelectMany(x => x.GetProperty("segments").EnumerateArray()).Select(x => x.GetProperty("text").GetString()));
        using var document = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var manifest = document.RootElement;
        Assert.Equal(2, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("utf16_code_unit", manifest.GetProperty("textIndexUnit").GetString());
        var ranges = manifest.GetProperty("ranges").EnumerateArray().ToArray();
        Assert.Equal(new[] { first, overlap, adjacent, empty }.Select(x => x.ToString()), ranges.Select(x => x.GetProperty("id").GetString()));
        Assert.Equal(new[] { "同名", "同名", "隣接", "空" }, ranges.Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(new[] { "人物", "章" }, ranges[0].GetProperty("tags").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("節", Assert.Single(ranges[1].GetProperty("tags").EnumerateArray()).GetString());
        Assert.Empty(ranges[2].GetProperty("tags").EnumerateArray());
        Assert.Equal("残す", Assert.Single(ranges[3].GetProperty("tags").EnumerateArray()).GetString());
        AssertRangePosition(ranges[0].GetProperty("start"), session, 1, 1);
        AssertRangePosition(ranges[0].GetProperty("end"), session, 1, 5);
        AssertRangePosition(ranges[1].GetProperty("start"), session, 1, 3);
        AssertRangePosition(ranges[1].GetProperty("end"), second, 1, 2);
        AssertRangePosition(ranges[2].GetProperty("start"), session, 1, 5);
        AssertRangePosition(ranges[2].GetProperty("end"), session, 1, 7);
        AssertRangePosition(ranges[3].GetProperty("start"), session, 2, 0);
        AssertRangePosition(ranges[3].GetProperty("end"), session, 2, 2);
        Assert.Equal(new[] { ($"{session}:1", 1, 4) }, RangeSpans(ranges[0]));
        Assert.Equal(new[] { ($"{session}:1", 3, 4), ($"{second}:1", 0, 2) }, RangeSpans(ranges[1]));
        Assert.Equal(new[] { ($"{session}:1", 5, 2) }, RangeSpans(ranges[2]));
        Assert.Empty(RangeSpans(ranges[3]));
        Assert.Equal(new[] { "😀AB", "ABC後次本", "C後", "" }, ranges.Select(x => ReadRangeText(x, rows)));
    }

    private static void AssertRangePosition(JsonElement position, long session, long sequence, int offset)
    {
        Assert.Equal(session.ToString(), position.GetProperty("sessionId").GetString());
        Assert.Equal(sequence, position.GetProperty("receiveSequence").GetInt64());
        Assert.Equal(offset, position.GetProperty("offsetUtf16").GetInt32());
    }

    private static (string Event, int Start, int Length)[] RangeSpans(JsonElement range) => range.GetProperty("spans").EnumerateArray()
        .Select(x => (x.GetProperty("eventId").GetString()!, x.GetProperty("startUtf16").GetInt32(), x.GetProperty("lengthUtf16").GetInt32())).ToArray();

    private static string ReadRangeText(JsonElement range, JsonElement[] rows) => string.Concat(RangeSpans(range).Select(span =>
    {
        var row = rows.Single(x => x.GetProperty("eventId").GetString() == span.Event);
        var segment = Assert.Single(row.GetProperty("segments").EnumerateArray(), x => x.GetProperty("startUtf16").GetInt32() <= span.Start
            && x.GetProperty("startUtf16").GetInt32() + x.GetProperty("lengthUtf16").GetInt32() >= span.Start + span.Length);
        return segment.GetProperty("text").GetString()!.Substring(span.Start - segment.GetProperty("startUtf16").GetInt32(), span.Length);
    }));

    // @test-value v2
    // kind = "invariant"
    // claim = "各出力条件で区間metadataは実収録segmentとの交差だけを示し除外区間・無一致区間・別分類や検索対象外の区間を漏らさない"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/30" }
    // fault = "元区間全体を収録済と扱う、選択区間metadataを無条件で出す、または除外穴を結合して本文や区間名を漏らす"
    // observable = "条件別実ZIPの本文・区間名・元境界・交差spansと空選択時のmetadata"
    // observation_boundary = "consumer"
    // scope = "filtered archive range privacy and exact slice mapping"
    // lifecycle = "permanent"
    // impact = "外部解析への除外本文・無関係な区間名とタグの持出しを防ぎ部分出力の誤解析を避ける"
    // distinction = "既存TXT条件testや型checkはZIP内の区間一覧と本文の対応を検証できない"
    // @end-test-value
    [Theory]
    [InlineData("tags")]
    [InlineData("range")]
    [InlineData("kind")]
    [InlineData("label")]
    [InlineData("unclassified")]
    [InlineData("speaker")]
    [InlineData("text")]
    [InlineData("empty")]
    public async Task FilteredArchiveMapsOnlyEmittedSlicesWithoutLeakingRanges(string filter)
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "abSECRETcd");
        await Append(session, 2, "hidden");
        var second = await _store.BeginCaptureSessionAsync(game);
        await Append(second, 1, "ef");
        var label = (await _store.GetCaptureLabelsAsync(game)).Single(x => x.Kind == CaptureKind.Dialogue);
        var item = (await _store.GetGameCaptureItemsAsync(game)).Single(x => x.Sequence == 1 && x.SessionId == session);
        await _store.SetCaptureLabelsAsync(session, [item.Id], label.Id);
        await _store.SetCaptureSpeakerAsync(session, item.Id, "花");
        await _store.SaveCaptureRangeAsync(game, "全域", new(session, 1, 0), new(second, 1, 2), ["keep"]);
        await _store.SaveCaptureRangeAsync(game, "左", new(session, 1, 0), new(session, 1, 2));
        await _store.SaveCaptureRangeAsync(game, "秘密区間", new(session, 1, 2), new(session, 1, 8), ["X", "秘密タグ"]);
        await _store.SaveCaptureRangeAsync(game, "右", new(session, 1, 8), new(session, 1, 10));
        await _store.SaveCaptureRangeAsync(game, "非収録", new(session, 2, 0), new(session, 2, 6), ["X"]);
        await _store.SaveCaptureRangeAsync(game, "次回", new(second, 1, 0), new(second, 1, 2));
        var selected = await _store.SaveCaptureRangeAsync(game, "選択", new(session, 1, 1), new(session, 1, 9), ["selected"]);
        var selection = new ScenarioArchiveSelection(IncludeTags: ["keep"], ExcludeTags: ["X"]);
        selection = filter switch
        {
            "range" => selection with { RangeId = selected },
            "kind" => selection with { Kind = CaptureKind.Dialogue },
            "label" => selection with { LabelId = label.Id },
            "unclassified" => selection with { UnclassifiedOnly = true },
            "speaker" => selection with { SpeakerSearch = "花" },
            "text" => selection with { TextSearch = "cd" },
            "empty" => selection with { RangeId = selected, TextSearch = "SECRET" },
            _ => selection
        };
        await _store.ExportScenarioArchiveAsync(game, selection, Output(filter));
        using var zip = ZipFile.OpenRead(Output(filter));
        var rows = await Rows(zip);
        using var document = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var manifest = document.RootElement;
        var ranges = manifest.GetProperty("ranges").EnumerateArray().ToArray();
        var expectedNames = filter switch
        {
            "tags" => new[] { "全域", "左", "選択", "右", "次回" },
            "range" or "kind" or "label" or "speaker" => ["全域", "左", "選択", "右"],
            "unclassified" => ["全域", "次回"],
            "text" => ["全域", "選択", "右"],
            _ => []
        };
        Assert.Equal(expectedNames, ranges.Select(x => x.GetProperty("name").GetString()));
        var expectedText = filter switch { "tags" => "abcdef", "range" => "bc", "unclassified" => "ef", "text" => "cd", "empty" => "", _ => "abcd" };
        Assert.Equal(expectedText, string.Concat(rows.SelectMany(x => x.GetProperty("segments").EnumerateArray()).Select(x => x.GetProperty("text").GetString())));
        Assert.DoesNotContain(JsonStrings(manifest), x => x.Contains("秘密区間", StringComparison.Ordinal));
        Assert.DoesNotContain(JsonStrings(manifest), x => x.Contains("秘密タグ", StringComparison.Ordinal));
        Assert.DoesNotContain(rows.SelectMany(JsonStrings), x => x.Contains("SECRET", StringComparison.Ordinal));
        var selectionInfo = manifest.GetProperty("selection");
        Assert.False(selectionInfo.TryGetProperty("range", out _));
        Assert.Equal(filter is "range" or "empty" ? selected.ToString() : null, selectionInfo.GetProperty("rangeId").GetString());
        if (filter == "empty") return;
        var broad = ranges.Single(x => x.GetProperty("name").GetString() == "全域");
        AssertRangePosition(broad.GetProperty("start"), session, 1, 0);
        AssertRangePosition(broad.GetProperty("end"), second, 1, 2);
        Assert.Equal(expectedText, ReadRangeText(broad, rows));
        var expectedSpans = filter switch
        {
            "tags" => new[] { ($"{session}:1", 0, 2), ($"{session}:1", 8, 2), ($"{second}:1", 0, 2) },
            "range" => [($"{session}:1", 1, 1), ($"{session}:1", 8, 1)],
            "unclassified" => [($"{second}:1", 0, 2)],
            "text" => [($"{session}:1", 8, 2)],
            _ => [($"{session}:1", 0, 2), ($"{session}:1", 8, 2)]
        };
        Assert.Equal(expectedSpans, RangeSpans(broad));
        foreach (var range in ranges)
        {
            var name = range.GetProperty("name").GetString();
            var expected = name switch { "全域" => expectedText, "左" => filter == "range" ? "b" : "ab", "右" => filter == "range" ? "c" : "cd", "選択" => filter == "text" ? "c" : "bc", _ => "ef" };
            Assert.Equal(expected, ReadRangeText(range, rows));
        }
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "保存区間がなくてもZIPは全本文を出し区間一覧は空にする"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/30" }
    // fault = "区間への割当を必須にして本文を落とすか架空の区間を作る"
    // observable = "実ZIPの区間一覧とJSONL本文"
    // observation_boundary = "consumer"
    // scope = "range-free whole-game archives"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task ArchiveWithoutRangesKeepsUnassignedText()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "未割当本文");
        await _store.ExportScenarioArchiveAsync(game, new(TextSearch: " ", IncludeTags: []), Output("no-ranges"));
        using var zip = ZipFile.OpenRead(Output("no-ranges"));
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.Empty(manifest.RootElement.GetProperty("ranges").EnumerateArray());
        Assert.Equal("未割当本文", Assert.Single(Assert.Single(await Rows(zip)).GetProperty("segments").EnumerateArray()).GetProperty("text").GetString());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
