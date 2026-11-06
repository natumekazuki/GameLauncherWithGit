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
        await _store.DeleteCaptureEventsAsync(game, [new(session, 1)]);
        Assert.True(File.Exists(shared.FilePath));
        Assert.False(File.Exists(privateImage.FilePath));
        Assert.Single(await _store.GetEventScreenshotsAsync(other, new(otherSession, 1)));
        await _store.ClearCaptureHistoryAsync(other);
        Assert.False(File.Exists(shared.FilePath));
        await Append(session, 2);
        var fresh = await _store.SaveScreenshotAsync(new(game, new(session, 2)), Image(), Time);
        Assert.NotEqual(shared.ImageId, fresh.ImageId);
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
        Assert.DoesNotContain("秘密前", row.GetRawText());
        Assert.DoesNotContain("本文のみ", row.GetRawText());
        Assert.DoesNotContain("名前のみ", row.GetRawText());
        Assert.False(row.TryGetProperty("rawText", out _));
        Assert.Equal(Time, row.GetProperty("images")[0].GetProperty("capturedAt").GetDateTimeOffset());
        Assert.Equal(image.ImageId, row.GetProperty("images")[0].GetProperty("imageId").GetString());
        Assert.Single(zip.Entries, x => x.FullName.StartsWith("images/"));
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var info = Assert.Single(manifest.RootElement.GetProperty("images").EnumerateArray());
        Assert.NotNull(zip.GetEntry(info.GetProperty("path").GetString()!));
        Assert.Equal("GameLauncherWithGit.scenario", manifest.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
