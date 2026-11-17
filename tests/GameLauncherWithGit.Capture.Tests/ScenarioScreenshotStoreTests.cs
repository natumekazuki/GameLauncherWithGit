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
    private async Task SeedCaptureLabel(long captureGameId, long labelId, string name, CaptureKind kind)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "library.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO CaptureLabels(Id,CaptureGameId,Name,Kind) VALUES($id,$game,$name,$kind)";
        command.Parameters.AddWithValue("$id", labelId);
        command.Parameters.AddWithValue("$game", captureGameId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$kind", (int)kind);
        await command.ExecuteNonQueryAsync();
    }
    private async Task AppendVisit(long session, IEnumerable<string> texts)
    {
        long sequence = 0;
        foreach (var text in texts)
            await _store.AppendCaptureAsync(session, new(++sequence, Time.AddSeconds(-sequence), Source, text, CaptureRole.Body),
                new() { OutsideIsNarration = true });
    }
    private string Output(string name) => Path.Combine(_directory, name + ".zip");
    private Task ExportFullAsync(long game, ScenarioArchiveSelection selection, string output) =>
        _store.ExportScenarioArchiveAsync(game, selection, output, ScenarioMaterialAccessLevel.Full);
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

    private static bool HasJsonProperty(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
            || HasJsonProperty(property.Value, name)),
        JsonValueKind.Array => value.EnumerateArray().Any(item => HasJsonProperty(item, name)),
        _ => false
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
    // claim = "指定イベントごとの画像IDと撮影時刻をPNGなしで返し添付なしを空として区別する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/32" }
    // fault = "画像metadata読取がPNGへ依存する、イベント間の添付関係を混ぜる、または未取得・不正参照を0件として返す"
    // observable = "要求順のevent keyと各画像ID・時刻、PNGを除いた後の結果、不正参照時の例外"
    // observation_boundary = "public-boundary"
    // scope = "event-scoped screenshot metadata reader"
    // lifecycle = "permanent"
    // impact = "本文・画像本体を開かず撮影位置と画像参照を特定できる"
    // distinction = "ZIP testは出力metadataを確認し、このtestはstorage readerがPNG非依存であることを確認する"
    // @end-test-value
    [Fact]
    public async Task ReadsScreenshotMetadataPerEventWithoutOpeningPng()
    {
        var (game, session) = await StartAsync();
        var (_, otherSession) = await StartAsync("other");
        await Append(session, 1, "一枚と二枚");
        await Append(session, 2, "添付なし");
        await Append(session, 3, "共有画像の別出現");
        await Append(otherSession, 1, "別ゲーム");

        var shared = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        var additional = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image('b'), Time.AddMinutes(1));
        await _store.SaveScreenshotAsync(new(game, new(session, 3)), Image(), Time.AddMinutes(2));

        var requested = new[] { new CaptureEventKey(session, 2), new(session, 1), new(session, 3), new(session, 1) };
        var metadata = await _store.GetEventScreenshotMetadataAsync(game, requested);

        Assert.Equal(new[] { requested[0], requested[1], requested[2] }, metadata.Select(x => x.Event));
        Assert.Empty(metadata[0].Images);
        Assert.Equal(new[] { shared.ImageId, additional.ImageId }, metadata[1].Images.Select(x => x.ImageId));
        Assert.Equal(new[] { Time, Time.AddMinutes(1) }, metadata[1].Images.Select(x => x.CapturedAt));
        Assert.Equal(shared.ImageId, Assert.Single(metadata[2].Images).ImageId);

        File.Delete(shared.FilePath);
        var withoutPng = await _store.GetEventScreenshotMetadataAsync(game, [new(session, 1), new(session, 3)]);
        Assert.Equal(new[] { shared.ImageId, additional.ImageId }, withoutPng[0].Images.Select(x => x.ImageId));
        Assert.Equal(shared.ImageId, Assert.Single(withoutPng[1].Images).ImageId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.GetEventScreenshotMetadataAsync(game, [new(session, 99)]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _store.GetEventScreenshotMetadataAsync(game, [new(otherSession, 1)]));
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
        await ExportFullAsync(game, new(range, Kind: CaptureKind.Dialogue, TextSearch: "abc", SpeakerSearch: "花"), output);
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
        Assert.Equal(3, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
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
        await ExportFullAsync(game, new(TextSearch: "一致"), output);
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
        var export = Task.Run(async () => { start.Wait(); await ExportFullAsync(game, new(), output); });
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
        await ExportFullAsync(game, new(), output);
        var original = await File.ReadAllBytesAsync(output);
        await Assert.ThrowsAsync<IOException>(() => ExportFullAsync(game, new(), output));
        Assert.Equal(original, await File.ReadAllBytesAsync(output));
        var lockedOutput = Output("locked");
        using (var locked = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => ExportFullAsync(game, new(), lockedOutput));
        Assert.False(File.Exists(lockedOutput));
        File.Delete(image.FilePath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time));
        var missingOutput = Output("missing");
        await Assert.ThrowsAsync<FileNotFoundException>(() => ExportFullAsync(game, new(), missingOutput));
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
            trigger.CommandText = """
                CREATE TRIGGER fail_tag BEFORE INSERT ON CaptureRangeTags
                WHEN NEW.TagId=(SELECT g.Id FROM ScenarioGameTags g JOIN CaptureRanges r ON r.CaptureGameId=g.CaptureGameId WHERE r.Id=NEW.RangeId AND g.Name='失敗')
                BEGIN SELECT RAISE(ABORT,'tag failure'); END
                """;
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
        await ExportFullAsync(game, selection, Output("tags"));
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
        await ExportFullAsync(game, selection with { IncludeTags = ["なし"] }, Output("empty-tags"));
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
        await ExportFullAsync(game, new(), Output("ranges"));
        using var zip = ZipFile.OpenRead(Output("ranges"));
        var rows = await Rows(zip);
        Assert.Equal(new[] { "前😀ABC後", "次本文", "区間外" }, rows.SelectMany(x => x.GetProperty("segments").EnumerateArray()).Select(x => x.GetProperty("text").GetString()));
        using var document = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var manifest = document.RootElement;
        Assert.Equal(3, manifest.GetProperty("schemaVersion").GetInt32());
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
        await ExportFullAsync(game, selection, Output(filter));
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
        await ExportFullAsync(game, new(TextSearch: " ", IncludeTags: []), Output("no-ranges"));
        using var zip = ZipFile.OpenRead(Output("no-ranges"));
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.Empty(manifest.RootElement.GetProperty("ranges").EnumerateArray());
        Assert.Equal("未割当本文", Assert.Single(Assert.Single(await Rows(zip)).GetProperty("segments").EnumerateArray()).GetProperty("text").GetString());
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "schema 3 manifestは選択イベントごとにraw位置と一意な画像ID対応を一度だけ示し本文のimagesと一致する"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/32" }
    // fault = "未タグの部分・重複区間でevent単位の添付を欠落・複製する、同一画像の別出現を統合する、または範囲外画像を漏らす"
    // observable = "実ZIPのscreenshotOccurrences、texts.jsonl画像ID、ranges.spans、manifest images一覧とPNG entry集合"
    // observation_boundary = "consumer"
    // scope = "archive screenshot occurrence metadata and filtered image references"
    // lifecycle = "permanent"
    // impact = "分析側が本文JSONLやPNGを開かず選択イベントの画像位置・件数を引ける"
    // distinction = "既存JSONLのみでは本文行の読取が必要で、既存range testは画像対応を検証しない"
    // @end-test-value
    [Fact]
    public async Task ArchiveManifestMapsSelectedEventPositionsToImagesAndZeroCounts()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "abcdef");
        await Append(session, 2, "gh");
        await Append(session, 3, "ij");
        await Append(session, 4, "範囲外");
        await _store.SplitCaptureEventAsync(session, 1, [2, 4]);

        var shared = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        var firstOnly = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image('b'), Time.AddMinutes(1));
        await _store.SaveScreenshotAsync(new(game, new(session, 3)), Image(), Time.AddMinutes(2));
        var outside = await _store.SaveScreenshotAsync(new(game, new(session, 4)), Image('c'), Time.AddMinutes(3));
        var selectedRange = await _store.SaveCaptureRangeAsync(game, "未タグ選択",
            new(session, 1, 1), new(session, 3, 1));
        await _store.SaveCaptureRangeAsync(game, "重なり",
            new(session, 1, 2), new(session, 2, 1));

        await ExportFullAsync(game, new(RangeId: selectedRange), Output("screenshot-occurrences"));
        using var zip = ZipFile.OpenRead(Output("screenshot-occurrences"));
        var rows = await Rows(zip);
        using var document = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var manifest = document.RootElement;
        Assert.Equal(3, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(new[] { "b", "cd", "ef", "gh", "i" }, rows.SelectMany(x => x.GetProperty("segments").EnumerateArray())
            .Select(x => x.GetProperty("text").GetString()));

        var occurrences = manifest.GetProperty("screenshotOccurrences").EnumerateArray().ToArray();
        Assert.Equal(new[] { $"{session}:1", $"{session}:2", $"{session}:3" },
            occurrences.Select(x => x.GetProperty("eventId").GetString()));
        Assert.Equal(new long[] { 1, 2, 3 }, occurrences.Select(x => x.GetProperty("receiveSequence").GetInt64()));
        Assert.All(occurrences, x => Assert.Equal(session.ToString(), x.GetProperty("sessionId").GetString()));
        Assert.Equal(new[] { shared.ImageId, firstOnly.ImageId }, occurrences[0].GetProperty("imageIds").EnumerateArray()
            .Select(x => x.GetString()));
        Assert.Empty(occurrences[1].GetProperty("imageIds").EnumerateArray());
        Assert.Equal(shared.ImageId, Assert.Single(occurrences[2].GetProperty("imageIds").EnumerateArray()).GetString());
        Assert.Equal(new[] { $"{session}:1", $"{session}:2", $"{session}:3" },
            occurrences.Select(x => x.GetProperty("eventId").GetString()).Distinct());

        foreach (var occurrence in occurrences)
        {
            var row = Assert.Single(rows, x => x.GetProperty("eventId").GetString() == occurrence.GetProperty("eventId").GetString());
            Assert.Equal(row.GetProperty("images").EnumerateArray().Select(x => x.GetProperty("imageId").GetString()),
                occurrence.GetProperty("imageIds").EnumerateArray().Select(x => x.GetString()));
        }

        var manifestImageIds = manifest.GetProperty("images").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToArray();
        Assert.Equal(new[] { shared.ImageId, firstOnly.ImageId }.Order(), manifestImageIds.Order());
        Assert.Equal(2, occurrences.Count(x => x.GetProperty("imageIds").EnumerateArray()
            .Any(id => id.GetString() == shared.ImageId)));
        Assert.DoesNotContain(occurrences.SelectMany(x => x.GetProperty("imageIds").EnumerateArray()),
            x => x.GetString() == outside.ImageId);
        Assert.Null(zip.GetEntry($"images/{outside.ImageId}.png"));
        Assert.NotNull(zip.GetEntry($"images/{shared.ImageId}.png"));
        Assert.Equal(1, zip.Entries.Count(x => x.FullName == $"images/{shared.ImageId}.png"));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ZIPのmaterial snapshotは3周の共通本文・一言分岐・再合流・周回別画像を保ち選択範囲と同じ版を示す"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "共通本文の出現を消す、分岐を共通化する、画像を別周へ混ぜる、選択外参照を出す、またはmanifestとmaterialの版がずれる"
    // observable = "material.jsonのcontents/occurrences/matches/version、manifestのmaterial参照、JSONLとの出現・画像対応、tag/UTF-16 partial選択"
    // observation_boundary = "consumer"
    // scope = "schema 3 ZIP common scenario material projection"
    // lifecycle = "permanent"
    // impact = "分析側が共通本文を一度だけ読み、選択中の各周・分岐・画像をraw位置から復元できる"
    // distinction = "reader testはsnapshot APIを検証し、このtestはZIP同梱projection・manifest連携・従来本文出力との一致を検証する"
    // @end-test-value
    [Fact]
    public async Task ArchiveMaterialPreservesVisitsBranchRejoinImageAndScopedSelections()
    {
        var (game, first) = await StartAsync();
        var common = new[] { "冒頭😀の短文", "長い会話のアンカーになる文章です", "続きの短文", "うん", "共通の後半へ戻る長い文章です", "次の場面", "結びの言葉" };
        var variant = common.Select(x => x == "うん" ? "いや" : x).ToArray();
        await AppendVisit(first, common);
        var second = await _store.BeginCaptureSessionAsync(game);
        await AppendVisit(second, variant);
        var third = await _store.BeginCaptureSessionAsync(game);
        await AppendVisit(third, common);

        var beforeImage = Output("material-before-image");
        await ExportFullAsync(game, new(), beforeImage);
        using var beforeZip = ZipFile.OpenRead(beforeImage);
        using var beforeManifestDocument = JsonDocument.Parse(beforeZip.GetEntry("manifest.json")!.Open());
        var beforeVersion = beforeManifestDocument.RootElement.GetProperty("materialVersion");

        var image = await _store.SaveScreenshotAsync(new(game, new(second, 4)), Image(), Time);
        var fullOutput = Output("material-all-visits");
        await ExportFullAsync(game, new(), fullOutput);
        using var fullZip = ZipFile.OpenRead(fullOutput);
        using var manifestDocument = JsonDocument.Parse(fullZip.GetEntry("manifest.json")!.Open());
        using var materialDocument = JsonDocument.Parse(fullZip.GetEntry("material.json")!.Open());
        var manifest = manifestDocument.RootElement;
        var material = materialDocument.RootElement;

        Assert.Equal(3, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("material.json", manifest.GetProperty("materialFile").GetString());
        Assert.True(manifest.GetProperty("matchingComplete").GetBoolean());
        Assert.True(material.GetProperty("matchingComplete").GetBoolean());
        Assert.Equal(manifest.GetProperty("game").GetProperty("captureGameId").GetString(),
            manifest.GetProperty("materialVersion").GetProperty("captureGameId").GetString());
        Assert.Equal(manifest.GetProperty("materialVersion").GetProperty("captureGameId").GetString(),
            material.GetProperty("version").GetProperty("captureGameId").GetString());
        Assert.Equal(manifest.GetProperty("materialVersion").GetProperty("revision").GetInt64(),
            material.GetProperty("version").GetProperty("revision").GetInt64());
        Assert.Equal(manifest.GetProperty("materialVersion").GetProperty("matchingRevision").GetInt64(),
            material.GetProperty("version").GetProperty("matchingRevision").GetInt64());
        Assert.True(manifest.GetProperty("materialVersion").GetProperty("revision").GetInt64() >
            beforeVersion.GetProperty("revision").GetInt64());

        var contents = material.GetProperty("contents").EnumerateArray().ToArray();
        var occurrences = material.GetProperty("occurrences").EnumerateArray().ToArray();
        var matches = material.GetProperty("matches").EnumerateArray().ToArray();
        Assert.All(contents, content => Assert.True(content.GetProperty("id").ValueKind == JsonValueKind.String));
        Assert.All(contents, content => Assert.True(content.GetProperty("kind").ValueKind is JsonValueKind.String or JsonValueKind.Null));
        Assert.All(occurrences, occurrence =>
        {
            Assert.Equal(JsonValueKind.String, occurrence.GetProperty("id").ValueKind);
            Assert.Equal(JsonValueKind.String, occurrence.GetProperty("contentId").ValueKind);
            var source = occurrence.GetProperty("source");
            Assert.Equal(JsonValueKind.String, source.GetProperty("captureGameId").ValueKind);
            Assert.Equal(JsonValueKind.String, source.GetProperty("sessionId").ValueKind);
            Assert.Equal(JsonValueKind.Number, source.GetProperty("receiveSequence").ValueKind);
            Assert.Equal(JsonValueKind.Number, source.GetProperty("startUtf16").ValueKind);
            Assert.Equal(JsonValueKind.Number, source.GetProperty("lengthUtf16").ValueKind);
            Assert.True(occurrence.GetProperty("labelId").ValueKind is JsonValueKind.String or JsonValueKind.Null);
            Assert.All(occurrence.GetProperty("images").EnumerateArray(), imageRef =>
                Assert.Equal(JsonValueKind.String, imageRef.GetProperty("imageId").ValueKind));
        });
        Assert.Equal(8, contents.Length);
        Assert.Equal(21, occurrences.Length);
        Assert.Equal(7, matches.Length);
        Assert.Equal(6, matches.Count(match => match.GetProperty("items").GetArrayLength() == 2));
        Assert.Equal(1, matches.Count(match => match.GetProperty("items").GetArrayLength() == 1));
        Assert.Contains(contents, content => content.GetProperty("text").GetString() == "うん");
        var branch = Assert.Single(contents, content => content.GetProperty("text").GetString() == "いや");
        Assert.DoesNotContain(matches, match => match.GetProperty("id").GetString() == branch.GetProperty("id").GetString());
        Assert.All(matches, match =>
        {
            Assert.Equal(JsonValueKind.String, match.GetProperty("id").ValueKind);
            Assert.All(match.GetProperty("items").EnumerateArray(), pair =>
            {
                foreach (var position in new[] { pair.GetProperty("representative"), pair.GetProperty("revisit") })
                {
                    Assert.Equal(JsonValueKind.String, position.GetProperty("captureGameId").ValueKind);
                    Assert.Equal(JsonValueKind.String, position.GetProperty("sessionId").ValueKind);
                    Assert.Equal(JsonValueKind.Number, position.GetProperty("receiveSequence").ValueKind);
                    Assert.Equal(JsonValueKind.Number, position.GetProperty("startUtf16").ValueKind);
                    Assert.Equal(JsonValueKind.Number, position.GetProperty("lengthUtf16").ValueKind);
                }
            });
        });

        foreach (var (session, expected) in new[] { (first, common), (second, variant), (third, common) })
        {
            var actual = occurrences.Where(occurrence => occurrence.GetProperty("source").GetProperty("sessionId").GetString() == session.ToString())
                .Select(occurrence => contents.Single(content => content.GetProperty("id").GetString() == occurrence.GetProperty("contentId").GetString())
                    .GetProperty("text").GetString()).ToArray();
            Assert.Equal(expected, actual);
        }

        var pictured = Assert.Single(occurrences, occurrence => occurrence.GetProperty("images").GetArrayLength() > 0);
        Assert.Equal(second.ToString(), pictured.GetProperty("source").GetProperty("sessionId").GetString());
        Assert.Equal(4, pictured.GetProperty("source").GetProperty("receiveSequence").GetInt64());
        Assert.Equal(image.ImageId, Assert.Single(pictured.GetProperty("images").EnumerateArray()).GetProperty("imageId").GetString());
        Assert.Equal(new[] { image.ImageId }, manifest.GetProperty("images").EnumerateArray().Select(x => x.GetProperty("id").GetString()));

        var rows = await Rows(fullZip);
        Assert.Equal(occurrences.Length, rows.Length);
        for (var index = 0; index < rows.Length; index++)
        {
            var occurrence = occurrences[index];
            var row = rows[index];
            Assert.Equal(occurrence.GetProperty("source").GetProperty("sessionId").GetString(), row.GetProperty("sessionId").GetString());
            Assert.Equal(occurrence.GetProperty("source").GetProperty("receiveSequence").GetInt64(), row.GetProperty("receiveSequence").GetInt64());
            Assert.Equal(occurrence.GetProperty("images").EnumerateArray().Select(x => x.GetProperty("imageId").GetString()),
                row.GetProperty("images").EnumerateArray().Select(x => x.GetProperty("imageId").GetString()));
            var segment = Assert.Single(row.GetProperty("segments").EnumerateArray());
            Assert.True(segment.GetProperty("kind").ValueKind is JsonValueKind.String or JsonValueKind.Null);
            if (segment.GetProperty("label").ValueKind == JsonValueKind.Object)
                Assert.Equal(JsonValueKind.String, segment.GetProperty("label").GetProperty("id").ValueKind);
        }

        await _store.SaveCaptureRangeAsync(game, "二周目", new(second, 1, 0), new(second, 7, variant[6].Length), ["second"]);
        await ExportFullAsync(game, new(IncludeTags: ["second"]), Output("material-second-visit"));
        using (var taggedZip = ZipFile.OpenRead(Output("material-second-visit")))
        using (var taggedMaterial = JsonDocument.Parse(taggedZip.GetEntry("material.json")!.Open()))
        {
            var selected = taggedMaterial.RootElement;
            Assert.Equal(7, selected.GetProperty("occurrences").GetArrayLength());
            Assert.All(selected.GetProperty("occurrences").EnumerateArray(), occurrence =>
                Assert.Equal(second.ToString(), occurrence.GetProperty("source").GetProperty("sessionId").GetString()));
            Assert.Equal(variant, selected.GetProperty("occurrences").EnumerateArray().Select(occurrence =>
                selected.GetProperty("contents").EnumerateArray().Single(content => content.GetProperty("id").GetString() == occurrence.GetProperty("contentId").GetString())
                    .GetProperty("text").GetString()));
        }

        await _store.SaveCaptureRangeAsync(game, "UTF-16 partial", new(second, 1, 2), new(second, 1, 4), ["partial"]);
        await ExportFullAsync(game, new(IncludeTags: ["partial"]), Output("material-partial"));
        using var partialZip = ZipFile.OpenRead(Output("material-partial"));
        using var partialMaterial = JsonDocument.Parse(partialZip.GetEntry("material.json")!.Open());
        var partial = partialMaterial.RootElement;
        var partialOccurrence = Assert.Single(partial.GetProperty("occurrences").EnumerateArray());
        Assert.Equal(second.ToString(), partialOccurrence.GetProperty("source").GetProperty("sessionId").GetString());
        Assert.Equal(1, partialOccurrence.GetProperty("source").GetProperty("receiveSequence").GetInt64());
        Assert.Equal(2, partialOccurrence.GetProperty("source").GetProperty("startUtf16").GetInt32());
        Assert.Equal(2, partialOccurrence.GetProperty("source").GetProperty("lengthUtf16").GetInt32());
        Assert.Equal("😀", partial.GetProperty("contents").EnumerateArray().Single().GetProperty("text").GetString());
        Assert.Empty(partial.GetProperty("occurrences").EnumerateArray().SelectMany(x => x.GetProperty("images").EnumerateArray()));
        Assert.DoesNotContain(JsonStrings(partial), text => common.Contains(text) || variant.Contains(text));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ZIP内materialとtextsは全公開kind値/nullおよび64bit label IDを精度損失なく同じ文字列表現で出し、版・出現量は数値で保つ"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動スクリーンショットと分析用ZIP" }
    // fault = "出力enumが数値化する、IDをJSON numberで出して桁落ちする、またはsequence/UTF-16位置を文字列化する"
    // observable = "完成ZIPのmaterial.json、manifest.json、texts.jsonlにあるkind・label/source/versionのJSON value kindと正確な値"
    // observation_boundary = "consumer"
    // scope = "scenario archive kind and identifier wire format"
    // lifecycle = "permanent"
    // impact = "外部JSON consumerがkindとラベル参照を一貫して読み、64bit IDを正確に保持できる"
    // distinction = "domain-level testsはenum/longの内部値を確認し、このtestは実ZIPへ書かれたJSON wire contractを確認する"
    // @end-test-value
    [Fact]
    public async Task ArchiveMaterialUsesUnifiedWireKindsAndIdentifiers()
    {
        const long largeLabelId = 9007199254740993;
        var (game, session) = await StartAsync();
        await SeedCaptureLabel(game, largeLabelId, "その他", CaptureKind.Other);
        await Append(session, 1, "dialogue text");
        await Append(session, 2, "narration text");
        await Append(session, 3, "other text");
        await Append(session, 4, "unclassified text");
        var items = await _store.GetCaptureItemsAsync(session, 0);
        var labels = await _store.GetCaptureLabelsAsync(game);
        await _store.SetCaptureLabelsAsync(session, [items[0].Id], Assert.Single(labels, label => label.Kind == CaptureKind.Dialogue).Id);
        await _store.SetCaptureLabelsAsync(session, [items[1].Id], Assert.Single(labels, label => label.Kind == CaptureKind.Narration).Id);
        await _store.SetCaptureLabelsAsync(session, [items[2].Id], largeLabelId);
        await _store.SetCaptureLabelsAsync(session, [items[3].Id], null);

        var output = Output("material-wire-types");
        await ExportFullAsync(game, new(), output);
        using var zip = ZipFile.OpenRead(output);
        using var manifestDocument = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        using var materialDocument = JsonDocument.Parse(zip.GetEntry("material.json")!.Open());
        var manifest = manifestDocument.RootElement;
        var material = materialDocument.RootElement;
        Assert.Equal(JsonValueKind.String, manifest.GetProperty("game").GetProperty("captureGameId").ValueKind);
        Assert.Equal(JsonValueKind.String, manifest.GetProperty("materialVersion").GetProperty("captureGameId").ValueKind);
        Assert.Equal(manifest.GetProperty("game").GetProperty("captureGameId").GetString(),
            manifest.GetProperty("materialVersion").GetProperty("captureGameId").GetString());
        Assert.Equal(manifest.GetProperty("materialVersion").GetProperty("captureGameId").GetString(),
            material.GetProperty("version").GetProperty("captureGameId").GetString());
        Assert.Equal(JsonValueKind.Number, material.GetProperty("version").GetProperty("revision").ValueKind);
        Assert.Equal(JsonValueKind.Number, material.GetProperty("version").GetProperty("matchingRevision").ValueKind);

        var contents = material.GetProperty("contents").EnumerateArray().ToArray();
        Assert.Equal(new string?[] { "Dialogue", "Narration", "Other", null }, contents.Select(content =>
            content.GetProperty("kind").ValueKind == JsonValueKind.Null ? null : content.GetProperty("kind").GetString()));
        var occurrences = material.GetProperty("occurrences").EnumerateArray().ToArray();
        Assert.Equal(4, occurrences.Length);
        Assert.All(occurrences, occurrence =>
        {
            var source = occurrence.GetProperty("source");
            Assert.Equal(JsonValueKind.String, source.GetProperty("captureGameId").ValueKind);
            Assert.Equal(game.ToString(), source.GetProperty("captureGameId").GetString());
            Assert.Equal(JsonValueKind.String, source.GetProperty("sessionId").ValueKind);
            Assert.Equal(session.ToString(), source.GetProperty("sessionId").GetString());
            Assert.Equal(JsonValueKind.Number, source.GetProperty("receiveSequence").ValueKind);
            Assert.Equal(JsonValueKind.Number, source.GetProperty("startUtf16").ValueKind);
            Assert.Equal(JsonValueKind.Number, source.GetProperty("lengthUtf16").ValueKind);
            Assert.True(occurrence.GetProperty("labelId").ValueKind is JsonValueKind.String or JsonValueKind.Null);
        });
        Assert.Equal(largeLabelId.ToString(System.Globalization.CultureInfo.InvariantCulture), occurrences[2].GetProperty("labelId").GetString());

        var rows = await Rows(zip);
        Assert.Equal(contents.Select(x => x.GetProperty("kind").ValueKind == JsonValueKind.Null ? null : x.GetProperty("kind").GetString()),
            rows.Select(row => Assert.Single(row.GetProperty("segments").EnumerateArray()).GetProperty("kind").ValueKind == JsonValueKind.Null
                ? null : Assert.Single(row.GetProperty("segments").EnumerateArray()).GetProperty("kind").GetString()));
        var thirdLabel = Assert.Single(rows[2].GetProperty("segments").EnumerateArray()).GetProperty("label");
        Assert.Equal(largeLabelId.ToString(System.Globalization.CultureInfo.InvariantCulture), thirdLabel.GetProperty("id").GetString());

        await ExportFullAsync(game, new(LabelId: largeLabelId), Output("material-wire-selected-label"));
        using var selectedZip = ZipFile.OpenRead(Output("material-wire-selected-label"));
        using var selectedManifestDocument = JsonDocument.Parse(selectedZip.GetEntry("manifest.json")!.Open());
        var selectedManifest = selectedManifestDocument.RootElement;
        Assert.Equal(3, selectedManifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.String, selectedManifest.GetProperty("selection").GetProperty("labelId").ValueKind);
        Assert.Equal(largeLabelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            selectedManifest.GetProperty("selection").GetProperty("labelId").GetString());
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "構造ZIPは本文・個別話者・検索語・画像path・PNGを省きつつ、完全ZIPと同じ選択資料の版/IDと区間話者集計を出力する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動スクリーンショットと分析用ZIP; 区間話者集計" }
    // fault = "構造出力から機密propertyを出す、完全版と違う資料を再採番する、またはPNG/本文ファイルを含める"
    // observable = "両ZIPの実JSON projectionとrangesの集計、manifest contentMode/selection fields、ZIP entry names、本文/個別話者/検索語の境界"
    // observation_boundary = "consumer"
    // scope = "structure and full scenario archive exports"
    // lifecycle = "permanent"
    // impact = "構造専用exportが本文情報を漏らさず、両export間で同じ選択済み出現を対応付けられる"
    // distinction = "共通projection testはreaderの公開JSONを確認し、このtestはさらにmanifestとZIP entryを含む実archive境界を確認する"
    // @end-test-value
    [Fact]
    public async Task StructureArchiveOmitsContentAndImageBodiesWhileSharingFullProjectionIds()
    {
        const string body = "BODY_MARKER_51fa82_visible_only_in_full";
        const string speaker = "SPEAKER_MARKER_c173a9_visible_only_in_full";
        var (game, session) = await StartAsync();
        await Append(session, 1, $"prefix {body} suffix");
        var item = Assert.Single(await _store.GetGameCaptureItemsAsync(game));
        var label = (await _store.GetCaptureLabelsAsync(game)).Single(x => x.Kind == CaptureKind.Dialogue);
        await _store.SetCaptureLabelsAsync(session, [item.Id], label.Id);
        await _store.SetCaptureSpeakerAsync(session, item.Id, speaker);
        var rangeId = await _store.SaveCaptureRangeAsync(game, "Structure range", new(session, 1, 0),
            new(session, 1, item.Text.Length), ["structure-tag"]);
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        var selection = new ScenarioArchiveSelection(RangeId: rangeId, TextSearch: "BODY_MARKER_51fa82",
            SpeakerSearch: "SPEAKER_MARKER_c173a9");
        var expectedStructure = await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Structure);
        var expectedFull = await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Full);

        var structurePath = Output("structure-material");
        var fullPath = Output("full-material");
        await _store.ExportScenarioArchiveAsync(game, selection, structurePath, ScenarioMaterialAccessLevel.Structure);
        await _store.ExportScenarioArchiveAsync(game, selection, fullPath, ScenarioMaterialAccessLevel.Full);

        using var structureZip = ZipFile.OpenRead(structurePath);
        using var fullZip = ZipFile.OpenRead(fullPath);
        Assert.Equal(new[] { "manifest.json", "material.json" }, structureZip.Entries.Select(x => x.FullName));
        Assert.NotNull(fullZip.GetEntry("texts.jsonl"));
        Assert.NotNull(fullZip.GetEntry($"images/{image.ImageId}.png"));
        using var structureManifestDocument = JsonDocument.Parse(structureZip.GetEntry("manifest.json")!.Open());
        using var structureMaterialDocument = JsonDocument.Parse(structureZip.GetEntry("material.json")!.Open());
        using var fullManifestDocument = JsonDocument.Parse(fullZip.GetEntry("manifest.json")!.Open());
        using var fullMaterialDocument = JsonDocument.Parse(fullZip.GetEntry("material.json")!.Open());
        var structureManifest = structureManifestDocument.RootElement;
        var structureMaterial = structureMaterialDocument.RootElement;
        var fullManifest = fullManifestDocument.RootElement;
        var fullMaterial = fullMaterialDocument.RootElement;
        var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Equal("structure", structureManifest.GetProperty("contentMode").GetString());
        Assert.Equal("full", fullManifest.GetProperty("contentMode").GetString());
        Assert.False(structureManifest.GetProperty("selection").TryGetProperty("textSearch", out _));
        Assert.False(structureManifest.GetProperty("selection").TryGetProperty("speakerSearch", out _));
        Assert.False(structureManifest.TryGetProperty("textFile", out _));
        Assert.False(HasJsonProperty(structureMaterial, "text"));
        Assert.False(HasJsonProperty(structureMaterial, "speaker"));
        Assert.False(HasJsonProperty(structureMaterial, "preview"));
        Assert.False(HasJsonProperty(structureMaterial, "path"));
        Assert.False(HasJsonProperty(structureMaterial, "thumbnail"));
        Assert.Equal("texts.jsonl", fullManifest.GetProperty("textFile").GetString());

        var expectedStructureJson = JsonSerializer.Serialize(expectedStructure, expectedStructure.GetType(), webOptions);
        var expectedFullJson = JsonSerializer.Serialize(expectedFull, expectedFull.GetType(), webOptions);
        using var expectedStructureDocument = JsonDocument.Parse(expectedStructureJson);
        using var expectedFullDocument = JsonDocument.Parse(expectedFullJson);
        Assert.Equal(expectedStructureDocument.RootElement.GetRawText(), structureMaterial.GetRawText());
        Assert.Equal(expectedFullDocument.RootElement.GetRawText(), fullMaterial.GetRawText());
        Assert.Equal(fullMaterial.GetProperty("version").GetRawText(), structureMaterial.GetProperty("version").GetRawText());
        Assert.Equal(fullMaterial.GetProperty("selectionVersion").GetRawText(), structureMaterial.GetProperty("selectionVersion").GetRawText());
        Assert.Equal(fullMaterial.GetProperty("contents").EnumerateArray().Select(x => x.GetProperty("id").GetString()),
            structureMaterial.GetProperty("contents").EnumerateArray().Select(x => x.GetProperty("id").GetString()));
        Assert.Equal(fullMaterial.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("id").GetString()),
            structureMaterial.GetProperty("occurrences").EnumerateArray().Select(x => x.GetProperty("id").GetString()));
        Assert.Equal(fullManifest.GetProperty("materialVersion").GetRawText(), structureManifest.GetProperty("materialVersion").GetRawText());
        Assert.Equal(fullMaterial.GetProperty("images").GetRawText(), structureMaterial.GetProperty("images").GetRawText());
        Assert.Equal(image.ImageId, Assert.Single(structureManifest.GetProperty("images").EnumerateArray()).GetProperty("id").GetString());
        Assert.False(structureManifest.GetProperty("images").EnumerateArray().Single().TryGetProperty("path", out _));
        var structureImageIds = structureMaterial.GetProperty("images").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.All(structureMaterial.GetProperty("occurrences").EnumerateArray(), occurrence =>
            Assert.All(occurrence.GetProperty("images").EnumerateArray(), imageReference =>
                Assert.Contains(imageReference.GetProperty("imageId").GetString(), structureImageIds)));
        Assert.Contains("structure-tag", structureMaterial.GetProperty("ranges").EnumerateArray()
            .SelectMany(x => x.GetProperty("tags").EnumerateArray()).Select(x => x.GetString()));
        var countedRange = Assert.Single(structureMaterial.GetProperty("ranges").EnumerateArray());
        var speakerCount = Assert.Single(countedRange.GetProperty("speakerCounts").EnumerateArray());
        Assert.Equal(speaker, speakerCount.GetProperty("speakerName").GetString());
        Assert.Equal(1, speakerCount.GetProperty("dialogueCount").GetInt32());
        Assert.Equal(0, countedRange.GetProperty("unknownSpeakerDialogueCount").GetInt32());
        Assert.Equal(fullMaterial.GetProperty("ranges").GetRawText(), structureMaterial.GetProperty("ranges").GetRawText());
        Assert.DoesNotContain(JsonStrings(structureMaterial), x => x.Contains(body, StringComparison.Ordinal));
        Assert.DoesNotContain(JsonStrings(structureManifest), x => x.Contains("BODY_MARKER_51fa82", StringComparison.Ordinal)
            || x.Contains("SPEAKER_MARKER_c173a9", StringComparison.Ordinal));
        Assert.Contains(JsonStrings(fullMaterial), x => x.Contains(body, StringComparison.Ordinal));
        Assert.Contains(JsonStrings(fullMaterial), x => x == speaker);
        Assert.Contains(JsonStrings(fullManifest), x => x == "BODY_MARKER_51fa82");
        Assert.Contains(JsonStrings(fullManifest), x => x == "SPEAKER_MARKER_c173a9");
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "ZIPのsceneFlowは全域では空場面と未使用tagを含み、絞込みでは交差range・参照tag・両端が残る手編集edgeだけをStructure/Full共通IDで出力する"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動場面フロー" }
    // fault = "ZIP writerが別版の場面を混ぜる、絞込み外の場面/tag/edgeを残す、全域の独立場面/tagを落とす、または実在しない接続を生成する"
    // observable = "Structure/Full ZIPのmaterial.json sceneFlow、selectionVersion、version、manifest.materialVersion、および選択本文のJSON値"
    // observation_boundary = "consumer"
    // scope = "full and structure scenario archive scene flow"
    // lifecycle = "permanent"
    // impact = "二権限exportで場面構造を安全に比較・参照し、選択範囲に属さない手動metadataを混入させない"
    // distinction = "共通projection testはreader DTOを検証し、このtestは実ZIP・manifest・選択scopeでwriterのJSON出力を検証する"
    // @end-test-value
    [Fact]
    public async Task SceneFlowArchiveSharesIdsAndClipsManualStructureToSelection()
    {
        const string selectedBody = "SELECTED_SCENE_FLOW_BODY_SENTINEL";
        const string outsideBody = "OUTSIDE_SCENE_FLOW_BODY_SENTINEL";
        var (game, session) = await StartAsync();
        await Append(session, 1, selectedBody);
        await Append(session, 2, outsideBody);
        var items = (await _store.GetGameCaptureItemsAsync(game)).OrderBy(x => x.Sequence).ToArray();
        var selectedRangeId = await _store.SaveCaptureRangeAsync(game, "selected range",
            new(session, items[0].Sequence, items[0].Start), new(session, items[0].Sequence, items[0].Start + items[0].Length), ["range tag"]);
        var outsideRangeId = await _store.SaveCaptureRangeAsync(game, "outside range",
            new(session, items[1].Sequence, items[1].Start), new(session, items[1].Sequence, items[1].Start + items[1].Length), ["outside range tag"]);
        var sceneTagId = await _store.GetOrCreateScenarioGameTagAsync(game, "scene tag");
        var outsideTagId = await _store.GetOrCreateScenarioGameTagAsync(game, "outside scene tag");
        var unusedTagId = await _store.GetOrCreateScenarioGameTagAsync(game, "unused tag");
        var firstSceneId = await _store.CreateScenarioSceneAsync(game,
            new("Selected first", "1", "Opening", 10, 20, [selectedRangeId], [sceneTagId]));
        var secondSceneId = await _store.CreateScenarioSceneAsync(game,
            new("Selected second", "1", "Opening", 30, 20, [selectedRangeId], [sceneTagId]));
        var outsideSceneId = await _store.CreateScenarioSceneAsync(game,
            new("Outside", "2", null, 50, 20, [outsideRangeId], [outsideTagId]));
        var independentSceneId = await _store.CreateScenarioSceneAsync(game,
            new("Independent", null, null, 70, 20, [], []));
        await _store.SetScenarioSceneConnectionAsync(game, firstSceneId, secondSceneId, true);
        await _store.SetScenarioSceneConnectionAsync(game, secondSceneId, outsideSceneId, true);
        await _store.SetScenarioSceneConnectionAsync(game, outsideSceneId, firstSceneId, true);

        async Task<JsonDocument> ExportMaterial(string fileName, ScenarioArchiveSelection selection, ScenarioMaterialAccessLevel level)
        {
            var path = Output(fileName);
            await _store.ExportScenarioArchiveAsync(game, selection, path, level);
            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("material.json")!.Open();
            return await JsonDocument.ParseAsync(stream);
        }

        using var allStructureDocument = await ExportMaterial("scene-flow-all-structure", new(), ScenarioMaterialAccessLevel.Structure);
        using var allFullDocument = await ExportMaterial("scene-flow-all-full", new(), ScenarioMaterialAccessLevel.Full);
        var selected = new ScenarioArchiveSelection(RangeId: selectedRangeId);
        using var selectedStructureDocument = await ExportMaterial("scene-flow-selected-structure", selected, ScenarioMaterialAccessLevel.Structure);
        using var selectedFullDocument = await ExportMaterial("scene-flow-selected-full", selected, ScenarioMaterialAccessLevel.Full);
        var selectedProjection = (ScenarioStructureProjection)await _store.GetScenarioProjectionAsync(game, selected, ScenarioMaterialAccessLevel.Structure);

        var allStructure = allStructureDocument.RootElement;
        var allFull = allFullDocument.RootElement;
        var selectedStructure = selectedStructureDocument.RootElement;
        var selectedFull = selectedFullDocument.RootElement;
        Assert.Equal(allFull.GetProperty("version").GetRawText(), allStructure.GetProperty("version").GetRawText());
        Assert.Equal(allFull.GetProperty("selectionVersion").GetRawText(), allStructure.GetProperty("selectionVersion").GetRawText());
        Assert.Equal(allFull.GetProperty("sceneFlow").GetRawText(), allStructure.GetProperty("sceneFlow").GetRawText());
        Assert.Equal(selectedFull.GetProperty("version").GetRawText(), selectedStructure.GetProperty("version").GetRawText());
        Assert.Equal(selectedFull.GetProperty("selectionVersion").GetRawText(), selectedStructure.GetProperty("selectionVersion").GetRawText());
        Assert.Equal(selectedFull.GetProperty("sceneFlow").GetRawText(), selectedStructure.GetProperty("sceneFlow").GetRawText());

        var allSceneFlow = allStructure.GetProperty("sceneFlow");
        var allScenes = allSceneFlow.GetProperty("scenes").EnumerateArray().ToArray();
        Assert.Equal(4, allScenes.Length);
        Assert.Contains(allScenes, scene => scene.GetProperty("id").GetString() == independentSceneId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && scene.GetProperty("rangeIds").GetArrayLength() == 0);
        Assert.Equal(new[] { firstSceneId, secondSceneId, outsideSceneId, independentSceneId }
                .Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal),
            allScenes.Select(scene => scene.GetProperty("id").GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(5, allSceneFlow.GetProperty("tags").GetArrayLength());
        Assert.Contains(allSceneFlow.GetProperty("tags").EnumerateArray(), tag =>
            tag.GetProperty("id").GetString() == unusedTagId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(3, allSceneFlow.GetProperty("connections").GetArrayLength());
        Assert.Equal(new[] { selectedBody, outsideBody }, allFull.GetProperty("contents").EnumerateArray()
            .Select(content => content.GetProperty("text").GetString()));

        var selectedSceneFlow = selectedStructure.GetProperty("sceneFlow");
        var selectedScenes = selectedSceneFlow.GetProperty("scenes").EnumerateArray().ToArray();
        Assert.Equal(new[] { firstSceneId, secondSceneId }.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            selectedScenes.Select(scene => scene.GetProperty("id").GetString()));
        Assert.All(selectedScenes, scene => Assert.Equal(new[] { selectedRangeId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            scene.GetProperty("rangeIds").EnumerateArray().Select(id => id.GetString())));
        Assert.Single(selectedSceneFlow.GetProperty("rangeTags").EnumerateArray());
        Assert.Equal(new[] { "range tag", "scene tag" }, selectedSceneFlow.GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        Assert.All(selectedScenes, scene => Assert.Equal(new[] { sceneTagId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            scene.GetProperty("tagIds").EnumerateArray().Select(id => id.GetString())));
        var selectedRangeTagId = selectedProjection.SceneFlow.RangeTags
            .Single(link => link.RangeId == selectedRangeId.ToString(System.Globalization.CultureInfo.InvariantCulture)).TagId;
        var rangeTag = Assert.Single(selectedSceneFlow.GetProperty("rangeTags").EnumerateArray());
        Assert.Equal(selectedRangeId.ToString(System.Globalization.CultureInfo.InvariantCulture), rangeTag.GetProperty("rangeId").GetString());
        Assert.Equal(selectedRangeTagId.ToString(System.Globalization.CultureInfo.InvariantCulture), rangeTag.GetProperty("tagId").GetString());
        var connection = Assert.Single(selectedSceneFlow.GetProperty("connections").EnumerateArray());
        Assert.Equal(firstSceneId.ToString(System.Globalization.CultureInfo.InvariantCulture), connection.GetProperty("fromSceneId").GetString());
        Assert.Equal(secondSceneId.ToString(System.Globalization.CultureInfo.InvariantCulture), connection.GetProperty("toSceneId").GetString());
        Assert.Equal(new[] { firstSceneId, secondSceneId }.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            selectedStructure.GetProperty("selectionVersion").GetProperty("sceneIds").EnumerateArray().Select(id => id.GetString()));
        var selectedScope = selectedStructure.GetProperty("selectionVersion");
        Assert.Equal(selectedProjection.SelectionVersion.OccurrenceIds, selectedScope.GetProperty("occurrenceIds").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(selectedProjection.SelectionVersion.RangeIds, selectedScope.GetProperty("rangeIds").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(selectedProjection.SelectionVersion.SceneIds, selectedScope.GetProperty("sceneIds").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal(selectedProjection.SelectionVersion.TagIds, selectedScope.GetProperty("tagIds").EnumerateArray().Select(id => id.GetString()));
        Assert.DoesNotContain(outsideTagId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            selectedSceneFlow.GetProperty("tags").EnumerateArray().Select(tag => tag.GetProperty("id").GetString()));
        Assert.DoesNotContain(unusedTagId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            selectedSceneFlow.GetProperty("tags").EnumerateArray().Select(tag => tag.GetProperty("id").GetString()));
        Assert.DoesNotContain(JsonStrings(selectedStructure), value => value.Contains(selectedBody, StringComparison.Ordinal)
            || value.Contains(outsideBody, StringComparison.Ordinal));
        Assert.Equal(new[] { selectedBody }, selectedFull.GetProperty("contents").EnumerateArray()
            .Select(content => content.GetProperty("text").GetString()));

        using var allStructureArchive = ZipFile.OpenRead(Output("scene-flow-all-structure"));
        using var allFullArchive = ZipFile.OpenRead(Output("scene-flow-all-full"));
        using var allStructureManifest = JsonDocument.Parse(allStructureArchive.GetEntry("manifest.json")!.Open());
        using var allFullManifest = JsonDocument.Parse(allFullArchive.GetEntry("manifest.json")!.Open());
        Assert.Equal(allFull.GetProperty("version").GetRawText(), allStructureManifest.RootElement.GetProperty("materialVersion").GetRawText());
        Assert.Equal(allFull.GetProperty("version").GetRawText(), allFullManifest.RootElement.GetProperty("materialVersion").GetRawText());
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "画像bytesはFull levelと一致する資料版・選択scope・選択内Occurrence/ImageId関連が揃った時だけ返る"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動スクリーンショットと分析用ZIP" }
    // fault = "構造権限・古いsnapshot・別selection・無関係な画像IDでPNG bytesを返す"
    // observable = "読取結果bytes、Structure/不一致requestの拒否、添付追加後に旧version requestが拒否されること"
    // observation_boundary = "public-boundary"
    // scope = "versioned selected occurrence screenshot byte reader"
    // lifecycle = "permanent"
    // impact = "構造キーや古い/別出現の参照が画像本体を取得できない"
    // distinction = "MCP auth testsはhostがtrusted levelを選ぶことを検証し、このtestはstoreのgate内scope/version/link検証を検証する"
    // @end-test-value
    [Fact]
    public async Task ReadsScenarioImageOnlyForFullMatchingVersionSelectionAndOccurrence()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "image access body");
        var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
        var selection = new ScenarioArchiveSelection();
        var full = (ScenarioFullProjection)await _store.GetScenarioProjectionAsync(game, selection, ScenarioMaterialAccessLevel.Full);
        var occurrence = Assert.Single(full.Occurrences);
        var request = new ScenarioMaterialImageRequest(game, selection, full.Version, full.SelectionVersion,
            occurrence.Id, image.ImageId);

        Assert.Equal(Png, await _store.ReadScenarioImageAsync(ScenarioMaterialAccessLevel.Full, request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _store.ReadScenarioImageAsync(ScenarioMaterialAccessLevel.Structure, request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _store.ReadScenarioImageAsync(ScenarioMaterialAccessLevel.Full, request with { ImageId = "not-linked" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.ReadScenarioImageAsync(ScenarioMaterialAccessLevel.Full,
                request with { Selection = new ScenarioArchiveSelection(TextSearch: "not present") }));

        await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image('b'), Time.AddMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.ReadScenarioImageAsync(ScenarioMaterialAccessLevel.Full, request));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "Structureまたは未定義levelの画像要求はgame・版・selection照会やmatching準備より先に拒否される"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 連続本文照合と共通資料" }
    // fault = "構造権限が無効なgame/referenceを渡して画像APIの準備・資料読取経路へ到達できる"
    // observable = "非Full levelでUnauthorizedAccessExceptionとなり、SQLite database fileが作成されない"
    // observation_boundary = "public-boundary"
    // scope = "scenario image API authorization order"
    // lifecycle = "permanent"
    // impact = "構造専用権限は対象の存在・version・画像関連を調べる前に画像取得を拒否する"
    // distinction = "共通image-access testは同snapshot内のFull version/selection/linkを検証し、このtestは権限拒否がDB準備に先行することを検証する"
    // @end-test-value
    [Fact]
    public async Task RejectsNonFullImageRequestsBeforeDatabaseOrMaterialPreparation()
    {
        var request = new ScenarioMaterialImageRequest(long.MinValue, new ScenarioArchiveSelection(),
            new ScenarioMaterialWireVersion("not-a-game-id", 10, 20), new ScenarioMaterialSelectionVersion([], [], [], []),
            "not-a-selected-occurrence", "not-an-image");
        var databasePath = Path.Combine(_directory, "library.db");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _store.ReadScenarioImageAsync(ScenarioMaterialAccessLevel.Structure, request));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _store.ReadScenarioImageAsync((ScenarioMaterialAccessLevel)int.MaxValue, request));

        Assert.False(File.Exists(databasePath));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "material JSON wire projection writes all 64-bit identifiers as exact strings, public kind names or null, and keeps revision/counter/UTF-16 quantities numeric"
    // oracle = { type = "contract", ref = "docs/design/scenario-extraction.md: 手動スクリーンショットと分析用ZIP" }
    // fault = "default enum/long serialization leaks numeric kinds or loses identifier precision, or stringifies version counters and source offsets"
    // observable = "serialized JSON property kinds and exact values for content, labels, version, occurrence and match pair references"
    // observation_boundary = "consumer"
    // scope = "scenario material archive JSON wire projection"
    // lifecycle = "permanent"
    // impact = "JSON consumers can compare every 64-bit ID exactly without confusing counters and UTF-16 positions with identifiers"
    // distinction = "ZIP integration tests verify projection values from the database; this synthetic exact-value test covers all enum kinds and IDs beyond JavaScript safe integer precision"
    // @end-test-value
    [Fact]
    public void ScenarioMaterialWireProjectionUnifiesIdentifiersKindsAndQuantities()
    {
        const long largeId = 9007199254740993;
        var first = new ScenarioOccurrenceRef(largeId, largeId + 1, 12, 2, 4);
        var revisit = new ScenarioOccurrenceRef(largeId, largeId + 2, 19, 6, 3);
        var snapshot = new ScenarioMaterialSnapshot(
            new(largeId, 41, 37), true,
            [new("content-dialogue", "セリフ", CaptureKind.Dialogue),
             new("content-narration", "地の文", CaptureKind.Narration),
             new("content-other", "その他", CaptureKind.Other),
             new("content-null", "未判定", null)],
            [new("occurrence-dialogue", first, "content-dialogue", "話者", largeId + 3, "ラベル", Time,
                 [new("image-01", Time)]),
             new("occurrence-revisit", revisit, "content-dialogue", null, null, null, Time.AddSeconds(1), []),
             new("occurrence-narration", first with { ReceiveSequence = 13 }, "content-narration", null, null, null, Time, []),
             new("occurrence-other", first with { ReceiveSequence = 14 }, "content-other", null, null, null, Time, []),
             new("occurrence-null", first with { ReceiveSequence = 15 }, "content-null", null, null, null, Time, [])],
            [new("match-dialogue", [new(first, revisit)])]);

        var json = JsonSerializer.Serialize(ScenarioMaterialWireAdapter.ToWire(snapshot), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var material = document.RootElement;
        var version = material.GetProperty("version");
        Assert.Equal(largeId.ToString(System.Globalization.CultureInfo.InvariantCulture), version.GetProperty("captureGameId").GetString());
        Assert.Equal(JsonValueKind.Number, version.GetProperty("revision").ValueKind);
        Assert.Equal(41, version.GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Number, version.GetProperty("matchingRevision").ValueKind);
        Assert.Equal(37, version.GetProperty("matchingRevision").GetInt64());
        Assert.Equal(new string?[] { "Dialogue", "Narration", "Other", null },
            material.GetProperty("contents").EnumerateArray().Select(content =>
                content.GetProperty("kind").ValueKind == JsonValueKind.Null ? null : content.GetProperty("kind").GetString()));

        var occurrences = material.GetProperty("occurrences").EnumerateArray().ToArray();
        var source = occurrences[0].GetProperty("source");
        Assert.Equal(largeId.ToString(System.Globalization.CultureInfo.InvariantCulture), source.GetProperty("captureGameId").GetString());
        Assert.Equal((largeId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), source.GetProperty("sessionId").GetString());
        Assert.Equal(12, source.GetProperty("receiveSequence").GetInt64());
        Assert.Equal(2, source.GetProperty("startUtf16").GetInt32());
        Assert.Equal(4, source.GetProperty("lengthUtf16").GetInt32());
        Assert.Equal((largeId + 3).ToString(System.Globalization.CultureInfo.InvariantCulture), occurrences[0].GetProperty("labelId").GetString());
        Assert.Equal(JsonValueKind.Null, occurrences[1].GetProperty("labelId").ValueKind);
        Assert.Equal("image-01", Assert.Single(occurrences[0].GetProperty("images").EnumerateArray()).GetProperty("imageId").GetString());

        var pair = Assert.Single(Assert.Single(material.GetProperty("matches").EnumerateArray()).GetProperty("items").EnumerateArray());
        Assert.Equal((largeId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            pair.GetProperty("representative").GetProperty("sessionId").GetString());
        Assert.Equal((largeId + 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
            pair.GetProperty("revisit").GetProperty("sessionId").GetString());
        Assert.Equal(19, pair.GetProperty("revisit").GetProperty("receiveSequence").GetInt64());
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "捕捉書込中のZIPはMatchingComplete=falseを示し画像関係変更後の同snapshot版をmanifestとmaterialへ一致させる"
    // oracle = { type = "contract", ref = "https://github.com/natumekazuki/GameLauncherWithGit/issues/31" }
    // fault = "未処理一致状態を完了扱いする、古い版を参照する、またはmanifestとmaterialで異なる版を示す"
    // observable = "実ZIPのmatchingComplete・version、未処理occurrences/contents、画像関係変更前後のRevision"
    // observation_boundary = "consumer"
    // scope = "deferred scenario matching and versioned ZIP export"
    // lifecycle = "permanent"
    // impact = "保存優先中の未処理資料を古い一致と誤認せず、再利用可能な同一snapshot版を示す"
    // distinction = "reader batch testは処理進行を検証し、このtestはZIP exportの保存優先状態と版参照を検証する"
    // @end-test-value
    [Fact]
    public async Task ArchiveMarksDeferredMatchingAndTracksVersionChanges()
    {
        var (game, session) = await StartAsync();
        await Append(session, 1, "未照合本文");
        var pendingWrites = typeof(SqliteGameLibraryStore).GetField("_captureWritesPending",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(pendingWrites);
        pendingWrites.SetValue(_store, 1);
        try
        {
            var beforeOutput = Output("material-pending-before-image");
            await ExportFullAsync(game, new(), beforeOutput);
            using var beforeZip = ZipFile.OpenRead(beforeOutput);
            using var beforeManifestDocument = JsonDocument.Parse(beforeZip.GetEntry("manifest.json")!.Open());
            using var beforeMaterialDocument = JsonDocument.Parse(beforeZip.GetEntry("material.json")!.Open());
            var beforeManifest = beforeManifestDocument.RootElement;
            var beforeMaterial = beforeMaterialDocument.RootElement;
            Assert.False(beforeManifest.GetProperty("matchingComplete").GetBoolean());
            Assert.False(beforeMaterial.GetProperty("matchingComplete").GetBoolean());
            Assert.Equal(1, beforeMaterial.GetProperty("contents").GetArrayLength());
            Assert.Empty(beforeMaterial.GetProperty("matches").EnumerateArray());
            var beforeRevision = beforeMaterial.GetProperty("version").GetProperty("revision").GetInt64();

            var image = await _store.SaveScreenshotAsync(new(game, new(session, 1)), Image(), Time);
            var afterOutput = Output("material-pending-after-image");
            await ExportFullAsync(game, new(), afterOutput);
            using var afterZip = ZipFile.OpenRead(afterOutput);
            using var afterManifestDocument = JsonDocument.Parse(afterZip.GetEntry("manifest.json")!.Open());
            using var afterMaterialDocument = JsonDocument.Parse(afterZip.GetEntry("material.json")!.Open());
            var afterManifest = afterManifestDocument.RootElement;
            var afterMaterial = afterMaterialDocument.RootElement;
            Assert.False(afterManifest.GetProperty("matchingComplete").GetBoolean());
            Assert.False(afterMaterial.GetProperty("matchingComplete").GetBoolean());
            Assert.True(afterMaterial.GetProperty("version").GetProperty("revision").GetInt64() > beforeRevision);
            Assert.Equal(afterManifest.GetProperty("materialVersion").GetProperty("revision").GetInt64(),
                afterMaterial.GetProperty("version").GetProperty("revision").GetInt64());
            Assert.Equal(image.ImageId, Assert.Single(Assert.Single(afterMaterial.GetProperty("occurrences").EnumerateArray())
                .GetProperty("images").EnumerateArray()).GetProperty("imageId").GetString());
        }
        finally
        {
            pendingWrites.SetValue(_store, 0);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
