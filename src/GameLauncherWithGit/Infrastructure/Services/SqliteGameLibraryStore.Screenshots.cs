using GameLauncherWithGit.Application.Models;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    private const string ImageHashAlgorithm = "sha256-dimensions-rgba8-v1";
    private readonly SemaphoreSlim _screenshotFilesLock = new(1, 1);
    private string ScreenshotPath(string id) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!, "scenario-images", id + ".png");

    public async Task<ScenarioScreenshot> SaveScreenshotAsync(ScreenshotTarget target, ScreenshotImageData image, DateTimeOffset capturedAt)
    {
        await _screenshotFilesLock.WaitAsync();
        try { return await SaveScreenshotCoreAsync(target, image, capturedAt); }
        finally { _screenshotFilesLock.Release(); }
    }

    private async Task<ScenarioScreenshot> SaveScreenshotCoreAsync(ScreenshotTarget target, ScreenshotImageData image, DateTimeOffset capturedAt)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Png.Length == 0
            || image.PixelHash.Length != 64 || !image.PixelHash.All(Uri.IsHexDigit))
            throw new ArgumentException("画像データが不正です。");
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        var proposedId = Guid.NewGuid().ToString("N");
        var proposedPath = ScreenshotPath(proposedId);
        Directory.CreateDirectory(Path.GetDirectoryName(proposedPath)!);
        var temporary = proposedPath + ".tmp";
        var temporaryCreated = false;
        string? createdPath = null;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporaryCreated = true;
                await output.WriteAsync(image.Png);
            }
            await using var transaction = connection.BeginTransaction();
            await using (var owner = CaptureCommand(connection, transaction, """
            SELECT e.RawText FROM CaptureEvents e JOIN CaptureSessions s ON s.Id=e.SessionId
            WHERE s.CaptureGameId=$game AND e.SessionId=$session AND e.ReceiveSequence=$sequence AND e.InputRole=1
            """, ("$game", target.CaptureGameId), ("$session", target.Event.SessionId), ("$sequence", target.Event.Sequence)))
            {
                if (await owner.ExecuteScalarAsync() is not string text || string.IsNullOrWhiteSpace(text))
                    throw new ArgumentException("撮影対象の本文が保存されていません。");
            }
            var hash = image.PixelHash.ToLowerInvariant();
            string? id;
            await using (var find = CaptureCommand(connection, transaction, "SELECT Id FROM CaptureImages WHERE PixelHash=$hash", ("$hash", hash)))
                id = await find.ExecuteScalarAsync() as string;
            if (id is null)
            {
                id = proposedId;
                var path = ScreenshotPath(id);
                File.Move(temporary, path);
                createdPath = path;
                await using var insert = CaptureCommand(connection, transaction,
                    "INSERT INTO CaptureImages(Id,PixelHash,Width,Height) VALUES($id,$hash,$width,$height)",
                    ("$id", id), ("$hash", hash), ("$width", image.Width), ("$height", image.Height));
                await insert.ExecuteNonQueryAsync();
            }
            else if (!File.Exists(ScreenshotPath(id)))
                throw new FileNotFoundException("保存済みの画像ファイルが見つかりません。", ScreenshotPath(id));
            await using var link = CaptureCommand(connection, transaction, """
                INSERT INTO CaptureEventImages(SessionId,ReceiveSequence,ImageId,CapturedAt) VALUES($session,$sequence,$image,$time)
                ON CONFLICT(SessionId,ReceiveSequence,ImageId) DO NOTHING
                """, ("$session", target.Event.SessionId), ("$sequence", target.Event.Sequence), ("$image", id), ("$time", capturedAt.ToString("O")));
            await link.ExecuteNonQueryAsync();
            var result = (await ReadEventScreenshotsAsync(connection, transaction, target.Event)).Single(x => x.ImageId == id);
            await transaction.CommitAsync();
            createdPath = null;
            return result;
        }
        finally
        {
            if (temporaryCreated) File.Delete(temporary);
            if (createdPath is not null) File.Delete(createdPath);
        }
    }

    public async Task<IReadOnlyList<ScenarioScreenshot>> GetEventScreenshotsAsync(long captureGameId, CaptureEventKey key)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction();
        if (await CaptureOwnerAsync(connection, transaction, key.SessionId) != captureGameId)
            throw new ArgumentException("このゲームの本文を選んでください。");
        return await ReadEventScreenshotsAsync(connection, transaction, key);
    }

    private async Task<List<ScenarioScreenshot>> ReadEventScreenshotsAsync(SqliteConnection connection, SqliteTransaction transaction, CaptureEventKey key)
    {
        await using var command = CaptureCommand(connection, transaction, """
            SELECT i.Id,i.Width,i.Height,i.PixelHash,l.CapturedAt FROM CaptureEventImages l
            JOIN CaptureImages i ON i.Id=l.ImageId WHERE l.SessionId=$session AND l.ReceiveSequence=$sequence
            ORDER BY l.CapturedAt,i.Id
            """, ("$session", key.SessionId), ("$sequence", key.Sequence));
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<ScenarioScreenshot>();
        while (await reader.ReadAsync()) result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetString(3), ScreenshotPath(reader.GetString(0)), DateTimeOffset.Parse(reader.GetString(4))));
        return result;
    }

    private static async Task<List<string>> ScreenshotCandidatesAsync(SqliteConnection connection, SqliteTransaction transaction, long gameId)
    {
        await using var command = CaptureCommand(connection, transaction, """
            SELECT DISTINCT l.ImageId FROM CaptureEventImages l JOIN CaptureSessions s ON s.Id=l.SessionId WHERE s.CaptureGameId=$game
            """, ("$game", gameId));
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task<List<string>> RemoveUnreferencedScreenshotsAsync(SqliteConnection connection, SqliteTransaction transaction, List<string> candidates)
    {
        var removed = new List<string>();
        foreach (var id in candidates)
        {
            await using var command = CaptureCommand(connection, transaction,
                "DELETE FROM CaptureImages WHERE Id=$id AND NOT EXISTS(SELECT 1 FROM CaptureEventImages WHERE ImageId=$id)", ("$id", id));
            if (await command.ExecuteNonQueryAsync() == 1) removed.Add(id);
        }
        return removed;
    }

    public async Task ExportScenarioArchiveAsync(long captureGameId, ScenarioArchiveSelection selection, string outputPath)
    {
        await _screenshotFilesLock.WaitAsync();
        try { await ExportScenarioArchiveCoreAsync(captureGameId, selection, outputPath); }
        finally { _screenshotFilesLock.Release(); }
    }

    private async Task ExportScenarioArchiveCoreAsync(long captureGameId, ScenarioArchiveSelection selection, string outputPath)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        // Read all selected data in one snapshot; the file gate retains its images while copying.
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var gameQuery = CaptureCommand(connection, transaction, "SELECT Title FROM CaptureGames WHERE Id=$game", ("$game", captureGameId));
        var title = await gameQuery.ExecuteScalarAsync() as string ?? throw new ArgumentException("ゲームの履歴が見つかりません。");
        CaptureRange? range = null;
        if (selection.RangeId is { } rangeId)
        {
            await using var rangeQuery = CaptureCommand(connection, transaction, """
                SELECT Name,StartSessionId,StartSequence,StartOffset,EndSessionId,EndSequence,EndOffset
                FROM CaptureRanges WHERE Id=$id AND CaptureGameId=$game
                """, ("$id", rangeId), ("$game", captureGameId));
            await using var reader = await rangeQuery.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) throw new ArgumentException("このゲームの区間を選んでください。");
            range = new(rangeId, captureGameId, reader.GetString(0), new(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3)),
                new(reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6)));
        }
        var items = new List<CaptureTextItem>();
        await using (var command = CaptureCommand(connection, transaction, CaptureItemQuery + """
             JOIN CaptureSessions s ON s.Id=i.SessionId WHERE s.CaptureGameId=$game
             AND ($label IS NULL OR l.Id=$label) AND ($unclassified=0 OR l.Id IS NULL) AND ($kind IS NULL OR l.Kind=$kind)
             ORDER BY i.SessionId,i.ReceiveSequence,i.StartUtf16
            """, ("$game", captureGameId), ("$label", selection.LabelId), ("$unclassified", selection.UnclassifiedOnly),
            ("$kind", selection.Kind is null ? null : (int)selection.Kind)))
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var item = ReadCaptureItem(reader);
                if (range is not null)
                {
                    if (item.EndPosition.CompareTo(range.Start) <= 0 || item.StartPosition.CompareTo(range.End) >= 0) continue;
                    var from = item.SessionId == range.Start.SessionId && item.Sequence == range.Start.Sequence ? Math.Max(item.Start, range.Start.Offset) : item.Start;
                    var to = item.SessionId == range.End.SessionId && item.Sequence == range.End.Sequence ? Math.Min(item.Start + item.Length, range.End.Offset) : item.Start + item.Length;
                    item = item with { Start = from, Length = to - from };
                }
                if (!string.IsNullOrWhiteSpace(selection.TextSearch) && !item.Text.Contains(selection.TextSearch.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(selection.SpeakerSearch) && item.Speaker?.Contains(selection.SpeakerSearch.Trim(), StringComparison.OrdinalIgnoreCase) != true) continue;
                items.Add(item);
            }
        }
        var images = new Dictionary<string, ScenarioScreenshot>();
        var rows = new List<object>();
        foreach (var group in items.GroupBy(x => new CaptureEventKey(x.SessionId, x.Sequence)))
        {
            var attached = await ReadEventScreenshotsAsync(connection, transaction, group.Key);
            foreach (var image in attached) images.TryAdd(image.ImageId, image);
            rows.Add(new { order = rows.Count + 1, eventId = $"{group.Key.SessionId}:{group.Key.Sequence}",
                sessionId = group.Key.SessionId.ToString(), receiveSequence = group.Key.Sequence, receivedAt = group.First().ReceivedAt,
                segments = group.Select(x => new { startUtf16 = x.Start, lengthUtf16 = x.Length, text = x.Text, kind = x.Kind?.ToString(),
                    speaker = x.Speaker, label = x.LabelId is null ? null : new { id = x.LabelId.ToString(), name = x.LabelName } }).ToArray(),
                images = attached.Select(x => new { imageId = x.ImageId, capturedAt = x.CapturedAt, scope = "capture_event", binding = "latest_received_body" }).ToArray() });
        }
        await transaction.CommitAsync();
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var temporaryCreated = false;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporaryCreated = true;
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await using (var manifest = zip.CreateEntry("manifest.json").Open())
                        await JsonSerializer.SerializeAsync(manifest, new { format = "GameLauncherWithGit.scenario", schemaVersion = 1, exportedAt = DateTimeOffset.UtcNow,
                            identifierScope = "archive", game = new { captureGameId = captureGameId.ToString(), title },
                            selection = new { scope = range is null ? "game" : "range", range, selection.LabelId, selection.UnclassifiedOnly, kind = selection.Kind?.ToString(), selection.TextSearch, selection.SpeakerSearch },
                            textFile = "texts.jsonl", textIndexUnit = "utf16_code_unit", imageHashAlgorithm = ImageHashAlgorithm,
                            images = images.Values.Select(x => new { id = x.ImageId, path = $"images/{x.ImageId}.png", mediaType = "image/png", width = x.Width, height = x.Height, pixelHash = x.PixelHash }) }, JsonOptions);
                    await using (var writer = new StreamWriter(zip.CreateEntry("texts.jsonl").Open(), new UTF8Encoding(false)))
                        foreach (var row in rows) await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
                    foreach (var image in images.Values)
                    {
                        await using var input = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await using var entry = zip.CreateEntry($"images/{image.ImageId}.png").Open();
                        await input.CopyToAsync(entry);
                    }
                }
            }
            File.Move(temporary, outputPath);
        }
        finally { if (temporaryCreated) File.Delete(temporary); }
    }
}
