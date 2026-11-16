using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore
{
    private const string ImageHashAlgorithm = "sha256-dimensions-rgba8-v1";
    private sealed record ScenarioScreenshotAsset(string ImageId, int Width, int Height, string PixelHash, string FilePath);
    private readonly SemaphoreSlim _screenshotFilesLock = new(1, 1);
    private static string FormatScenarioId(long id) => id.ToString(CultureInfo.InvariantCulture);
    private string ScreenshotPath(string id) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!, "scenario-images", id + ".png");

    public async Task<ScenarioScreenshot> SaveScreenshotAsync(ScreenshotTarget target, ScreenshotImageData image, DateTimeOffset capturedAt, CancellationToken cancellationToken = default)
    {
        await _screenshotFilesLock.WaitAsync(cancellationToken);
        try { return await SaveScreenshotCoreAsync(target, image, capturedAt, cancellationToken); }
        finally { _screenshotFilesLock.Release(); }
    }

    private async Task<ScenarioScreenshot> SaveScreenshotCoreAsync(ScreenshotTarget target, ScreenshotImageData image, DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Png.Length == 0
            || image.PixelHash.Length != 64 || !image.PixelHash.All(Uri.IsHexDigit))
            throw new ArgumentException("画像データが不正です。");
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
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
                await output.WriteAsync(image.Png, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            await using var link = CaptureCommand(connection, transaction, """
                INSERT INTO CaptureEventImages(SessionId,ReceiveSequence,ImageId,CapturedAt) VALUES($session,$sequence,$image,$time)
                ON CONFLICT(SessionId,ReceiveSequence,ImageId) DO NOTHING
                """, ("$session", target.Event.SessionId), ("$sequence", target.Event.Sequence), ("$image", id), ("$time", capturedAt.ToString("O")));
            await link.ExecuteNonQueryAsync();
            var result = (await ReadEventScreenshotsAsync(connection, transaction, target.Event)).Single(x => x.ImageId == id);
            cancellationToken.ThrowIfCancellationRequested();
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

    public async Task<IReadOnlyList<ScenarioScreenshotEventMetadata>> GetEventScreenshotMetadataAsync(long captureGameId,
        IReadOnlyCollection<CaptureEventKey> events, CancellationToken cancellationToken = default)
    {
        var keys = events.Distinct().ToArray();
        if (keys.Length == 0) return [];

        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var result = await ReadEventScreenshotMetadataAsync(connection, transaction, captureGameId, keys, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<IReadOnlyList<ScenarioScreenshotEventMetadata>> ReadEventScreenshotMetadataAsync(
        SqliteConnection connection, SqliteTransaction transaction, long captureGameId,
        IReadOnlyList<CaptureEventKey> events, CancellationToken cancellationToken)
    {
        const int batchSize = 400;
        var images = events.Select(_ => new List<ScenarioScreenshotReference>()).ToArray();
        for (var offset = 0; offset < events.Count; offset += batchSize)
        {
            var batchCount = Math.Min(batchSize, events.Count - offset);
            var values = new List<string>(batchCount);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var index = 0; index < batchCount; index++)
            {
                var ordinal = offset + index;
                var key = events[ordinal];
                values.Add($"({ordinal},$session{index},$sequence{index})");
                command.Parameters.AddWithValue($"$session{index}", key.SessionId);
                command.Parameters.AddWithValue($"$sequence{index}", key.Sequence);
            }
            command.CommandText = $"""
                WITH requested(Ordinal,SessionId,ReceiveSequence) AS (VALUES {string.Join(",", values)})
                SELECT r.Ordinal,s.CaptureGameId,e.ReceiveSequence,l.ImageId,l.CapturedAt
                FROM requested r
                LEFT JOIN CaptureSessions s ON s.Id=r.SessionId
                LEFT JOIN CaptureEvents e ON e.SessionId=r.SessionId AND e.ReceiveSequence=r.ReceiveSequence
                LEFT JOIN CaptureEventImages l ON l.SessionId=r.SessionId AND l.ReceiveSequence=r.ReceiveSequence
                WHERE s.CaptureGameId=$game
                ORDER BY r.Ordinal,l.CapturedAt,l.ImageId
                """;
            command.Parameters.AddWithValue("$game", captureGameId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var found = new HashSet<int>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var ordinal = reader.GetInt32(0);
                if (reader.IsDBNull(1) || reader.GetInt64(1) != captureGameId || reader.IsDBNull(2))
                    throw new ArgumentException("このゲームの本文を選んでください。");
                found.Add(ordinal);
                if (!reader.IsDBNull(3))
                    images[ordinal].Add(new(reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
            }
            if (found.Count != batchCount)
                throw new ArgumentException("このゲームの本文を選んでください。");
        }

        return events.Select((key, index) => new ScenarioScreenshotEventMetadata(key, images[index])).ToArray();
    }

    public async Task<IReadOnlyDictionary<CaptureEventKey, int>> GetScreenshotCountsAsync(long captureGameId, IReadOnlyCollection<CaptureEventKey> events)
    {
        var keys = events.Distinct().ToArray();
        var counts = new Dictionary<CaptureEventKey, int>();
        if (keys.Length == 0) return counts;
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var command = connection.CreateCommand();
        var values = new List<string>();
        for (var i = 0; i < keys.Length; i++)
        {
            values.Add($"($session{i},$sequence{i})");
            command.Parameters.AddWithValue($"$session{i}", keys[i].SessionId);
            command.Parameters.AddWithValue($"$sequence{i}", keys[i].Sequence);
        }
        command.CommandText = $"""
            WITH requested(SessionId,ReceiveSequence) AS (VALUES {string.Join(",", values)})
            SELECT r.SessionId,r.ReceiveSequence,s.CaptureGameId,COUNT(l.ImageId)
            FROM requested r LEFT JOIN CaptureSessions s ON s.Id=r.SessionId
            LEFT JOIN CaptureEventImages l ON l.SessionId=r.SessionId AND l.ReceiveSequence=r.ReceiveSequence
            GROUP BY r.SessionId,r.ReceiveSequence,s.CaptureGameId
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(2) || reader.GetInt64(2) != captureGameId)
                throw new ArgumentException("このゲームの本文を選んでください。");
            counts.Add(new(reader.GetInt64(0), reader.GetInt64(1)), reader.GetInt32(3));
        }
        return counts;
    }

    public async Task<ScreenshotCleanupResult> CleanupUnreferencedScreenshotsAsync()
    {
        await _screenshotFilesLock.WaitAsync();
        try
        {
            await EnsureInitializedAsync(default);
            await using var connection = await OpenConnectionAsync(default);
            var candidates = await ReadUnreferencedScreenshotsAsync(connection, null);
            return await CleanupUnreferencedScreenshotsCoreAsync(connection, candidates);
        }
        finally { _screenshotFilesLock.Release(); }
    }

    private static async Task<List<string>> ReadUnreferencedScreenshotsAsync(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var candidates = new List<string>();
        await using (var query = CaptureCommand(connection, transaction,
            "SELECT Id FROM CaptureImages WHERE NOT EXISTS(SELECT 1 FROM CaptureEventImages WHERE ImageId=CaptureImages.Id)"))
        {
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync()) candidates.Add(reader.GetString(0));
        }
        return candidates;
    }

    private async Task<ScreenshotCleanupResult> CleanupUnreferencedScreenshotsCoreAsync(SqliteConnection connection, List<string> candidates)
    {
        var failures = new List<string>();
        foreach (var id in candidates)
        {
            try
            {
                File.Delete(ScreenshotPath(id));
                await using var command = CaptureCommand(connection, null,
                    "DELETE FROM CaptureImages WHERE Id=$id AND NOT EXISTS(SELECT 1 FROM CaptureEventImages WHERE ImageId=$id)", ("$id", id));
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                failures.Add(ex.Message);
            }
        }
        return new(failures.Count, failures);
    }

    public async Task ExportScenarioArchiveAsync(long captureGameId, ScenarioArchiveSelection selection, string outputPath,
        ScenarioMaterialAccessLevel accessLevel, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(accessLevel)) throw new ArgumentOutOfRangeException(nameof(accessLevel));
        await PrepareScenarioMaterialAsync(captureGameId, cancellationToken);
        await _screenshotFilesLock.WaitAsync(cancellationToken);
        try { await ExportScenarioArchiveCoreAsync(captureGameId, selection, outputPath, accessLevel, cancellationToken); }
        finally { _screenshotFilesLock.Release(); }
    }

    public async Task<byte[]> ReadScenarioImageAsync(ScenarioMaterialAccessLevel level, ScenarioMaterialImageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (level != ScenarioMaterialAccessLevel.Full)
            throw new UnauthorizedAccessException("画像本体の読取権限がありません。");
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SelectionVersion);
        await PrepareScenarioMaterialAsync(request.CaptureGameId, cancellationToken);
        await _screenshotFilesLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = connection.BeginTransaction(deferred: true);
            var ranges = await ReadCaptureRangesAsync(connection, transaction, request.CaptureGameId);
            var items = await ReadCaptureExportItemsAsync(connection, transaction, request.CaptureGameId, request.Selection, ranges);
            var material = await ReadScenarioMaterialAsync(connection, transaction, request.CaptureGameId,
                request.Selection, ranges, items, cancellationToken);
            var eventKey = ScenarioMaterialImageAccess.Validate(material, level, request);
            var image = (await ReadEventScreenshotsAsync(connection, transaction, eventKey))
                .SingleOrDefault(x => string.Equals(x.ImageId, request.ImageId, StringComparison.Ordinal))
                ?? throw new UnauthorizedAccessException("選択した出現の添付画像ではありません。");
            var bytes = await File.ReadAllBytesAsync(image.FilePath, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return bytes;
        }
        finally { _screenshotFilesLock.Release(); }
    }

    private async Task ExportScenarioArchiveCoreAsync(long captureGameId, ScenarioArchiveSelection selection, string outputPath,
        ScenarioMaterialAccessLevel accessLevel, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // Read all selected data in one snapshot; the file gate retains its images while copying.
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var gameQuery = CaptureCommand(connection, transaction, "SELECT Title FROM CaptureGames WHERE Id=$game", ("$game", captureGameId));
        var title = await gameQuery.ExecuteScalarAsync(cancellationToken) as string ?? throw new ArgumentException("ゲームの履歴が見つかりません。");
        var ranges = await ReadCaptureRangesAsync(connection, transaction, captureGameId);
        var items = await ReadCaptureExportItemsAsync(connection, transaction, captureGameId, selection, ranges);
        var material = await ReadScenarioMaterialAsync(connection, transaction, captureGameId, selection, ranges, items, cancellationToken);
        var materialProjection = ScenarioMaterialWireAdapter.ToWire(material, accessLevel);
        var includeAllRanges = selection.RangeId is null && selection.LabelId is null && !selection.UnclassifiedOnly
            && selection.Kind is null && string.IsNullOrWhiteSpace(selection.TextSearch) && string.IsNullOrWhiteSpace(selection.SpeakerSearch)
            && NormalizeCaptureTags(selection.IncludeTags).Length == 0 && NormalizeCaptureTags(selection.ExcludeTags).Length == 0;
        var archiveRanges = CreateArchiveRanges(ranges, items, includeAllRanges);
        var contents = accessLevel == ScenarioMaterialAccessLevel.Full
            ? material.Contents.ToDictionary(x => x.Id, StringComparer.Ordinal)
            : null;
        var eventGroups = material.Occurrences.GroupBy(x => new CaptureEventKey(x.Source.SessionId, x.Source.ReceiveSequence)).ToArray();
        var imageReferences = eventGroups.SelectMany(x => x.SelectMany(occurrence => occurrence.Images)).ToArray();
        var images = accessLevel == ScenarioMaterialAccessLevel.Full
            ? await ReadScreenshotAssetsAsync(connection, transaction, imageReferences, cancellationToken)
            : new Dictionary<string, ScenarioScreenshotAsset>(StringComparer.Ordinal);
        var rows = new List<object>();
        var screenshotOccurrences = new List<object>();
        foreach (var group in eventGroups)
        {
            var key = group.Key;
            var attached = group.SelectMany(x => x.Images).GroupBy(x => x.ImageId).Select(x => x.First()).ToArray();
            var eventId = $"{FormatScenarioId(group.Key.SessionId)}:{FormatScenarioId(group.Key.Sequence)}";
            screenshotOccurrences.Add(new { eventId, sessionId = FormatScenarioId(key.SessionId),
                receiveSequence = key.Sequence, imageIds = attached.Select(x => x.ImageId).ToArray() });
            if (accessLevel == ScenarioMaterialAccessLevel.Full)
            {
                rows.Add(new { order = rows.Count + 1, eventId,
                    sessionId = FormatScenarioId(key.SessionId), receiveSequence = key.Sequence, receivedAt = group.First().ReceivedAt,
                    segments = group.Select(x => new { startUtf16 = x.Source.StartUtf16, lengthUtf16 = x.Source.LengthUtf16,
                        text = contents![x.ContentId].Text,
                        kind = ScenarioMaterialWireAdapter.KindName(contents[x.ContentId].Kind),
                        speaker = x.Speaker, label = x.LabelId is null ? null : new { id = FormatScenarioId(x.LabelId.Value), name = x.LabelName } }).ToArray(),
                    images = attached.Select(x => new { imageId = x.ImageId, capturedAt = x.CapturedAt, scope = "capture_event", binding = "latest_received_body" }).ToArray() });
            }
        }
        await transaction.CommitAsync(cancellationToken);
        var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var temporaryCreated = false;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                temporaryCreated = true;
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var manifest = CreateScenarioArchiveManifest(captureGameId, title, selection, archiveRanges, screenshotOccurrences,
                        materialProjection, images, accessLevel);
                    await using (var manifestStream = zip.CreateEntry("manifest.json").Open())
                        await JsonSerializer.SerializeAsync(manifestStream, manifest, manifest.GetType(), JsonOptions, cancellationToken);
                    await using (var materialFile = zip.CreateEntry("material.json").Open())
                        await JsonSerializer.SerializeAsync(materialFile, materialProjection, materialProjection.GetType(), JsonOptions, cancellationToken);
                    if (accessLevel == ScenarioMaterialAccessLevel.Full)
                    {
                        await using (var writer = new StreamWriter(zip.CreateEntry("texts.jsonl").Open(), new UTF8Encoding(false)))
                            foreach (var row in rows) await writer.WriteLineAsync(JsonSerializer.Serialize(row, JsonOptions));
                        foreach (var image in images.Values)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await using var input = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            await using var entry = zip.CreateEntry($"images/{image.ImageId}.png").Open();
                            await input.CopyToAsync(entry, cancellationToken);
                        }
                    }
                }
            }
            File.Move(temporary, outputPath);
        }
        finally { if (temporaryCreated) File.Delete(temporary); }
    }

    private static object CreateScenarioArchiveManifest(long captureGameId, string title, ScenarioArchiveSelection selection,
        object[] archiveRanges, List<object> screenshotOccurrences, ScenarioMaterialProjection materialProjection,
        IReadOnlyDictionary<string, ScenarioScreenshotAsset> images, ScenarioMaterialAccessLevel accessLevel)
    {
        var common = new
        {
            format = "GameLauncherWithGit.scenario",
            schemaVersion = 3,
            exportedAt = DateTimeOffset.UtcNow,
            identifierScope = "archive",
            game = new { captureGameId = FormatScenarioId(captureGameId), title }
        };
        if (accessLevel == ScenarioMaterialAccessLevel.Structure)
        {
            var structure = (ScenarioStructureProjection)materialProjection;
            return new
            {
                common.format,
                common.schemaVersion,
                common.exportedAt,
                common.identifierScope,
                common.game,
                contentMode = "structure",
                selection = new
                {
                    scope = selection.RangeId is null ? "game" : "range",
                    rangeId = selection.RangeId is long structureRangeId ? FormatScenarioId(structureRangeId) : null,
                    labelId = selection.LabelId is long structureLabelId ? FormatScenarioId(structureLabelId) : null,
                    selection.UnclassifiedOnly,
                    kind = ScenarioMaterialWireAdapter.KindName(selection.Kind),
                    includeTags = NormalizeCaptureTags(selection.IncludeTags),
                    excludeTags = NormalizeCaptureTags(selection.ExcludeTags)
                },
                ranges = archiveRanges,
                screenshotOccurrences,
                materialFile = "material.json",
                materialVersion = structure.Version,
                matchingComplete = structure.MatchingComplete,
                imageHashAlgorithm = ImageHashAlgorithm,
                images = structure.Images.Select(x => new { id = x.Id, mediaType = "image/png", width = x.Width, height = x.Height, pixelHash = x.PixelHash }).ToArray()
            };
        }

        var full = (ScenarioFullProjection)materialProjection;
        return new
        {
            common.format,
            common.schemaVersion,
            common.exportedAt,
            common.identifierScope,
            common.game,
            contentMode = "full",
            selection = new
            {
                scope = selection.RangeId is null ? "game" : "range",
                rangeId = selection.RangeId is long fullRangeId ? FormatScenarioId(fullRangeId) : null,
                labelId = selection.LabelId is long fullLabelId ? FormatScenarioId(fullLabelId) : null,
                selection.UnclassifiedOnly,
                kind = ScenarioMaterialWireAdapter.KindName(selection.Kind),
                selection.TextSearch,
                selection.SpeakerSearch,
                includeTags = NormalizeCaptureTags(selection.IncludeTags),
                excludeTags = NormalizeCaptureTags(selection.ExcludeTags)
            },
            ranges = archiveRanges,
            screenshotOccurrences,
            materialFile = "material.json",
            materialVersion = full.Version,
            matchingComplete = full.MatchingComplete,
            textFile = "texts.jsonl",
            textIndexUnit = "utf16_code_unit",
            imageHashAlgorithm = ImageHashAlgorithm,
            images = images.Values.Select(x => new { id = x.ImageId, path = $"images/{x.ImageId}.png", mediaType = "image/png", width = x.Width, height = x.Height, pixelHash = x.PixelHash }).ToArray()
        };
    }

    private async Task<Dictionary<string, ScenarioScreenshotAsset>> ReadScreenshotAssetsAsync(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyCollection<ScenarioOccurrenceImage> imageReferences, CancellationToken cancellationToken = default)
    {
        var imageIds = imageReferences.Select(x => x.ImageId).Distinct().ToArray();
        var result = new Dictionary<string, ScenarioScreenshotAsset>(StringComparer.Ordinal);
        const int batchSize = 400;
        for (var offset = 0; offset < imageIds.Length; offset += batchSize)
        {
            var batchCount = Math.Min(batchSize, imageIds.Length - offset);
            var values = new List<string>(batchCount);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            for (var index = 0; index < batchCount; index++)
            {
                var ordinal = offset + index;
                values.Add($"({ordinal},$image{index})");
                command.Parameters.AddWithValue($"$image{index}", imageIds[ordinal]);
            }
            command.CommandText = $"""
                WITH requested(Ordinal,ImageId) AS (VALUES {string.Join(",", values)})
                SELECT i.Id,i.Width,i.Height,i.PixelHash FROM requested r
                LEFT JOIN CaptureImages i ON i.Id=r.ImageId
                ORDER BY r.Ordinal
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var found = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0))
                    throw new InvalidOperationException("ZIPの添付metadataが参照する画像が見つかりません。");
                var id = reader.GetString(0);
                found.Add(id);
                result.Add(id, new(id, reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), ScreenshotPath(id)));
            }
            if (found.Count != batchCount)
                throw new InvalidOperationException("ZIPの添付metadataが参照する画像が見つかりません。");
        }
        return result;
    }

    private static object[] CreateArchiveRanges(IReadOnlyList<CaptureRange> ranges, IReadOnlyList<CaptureTextItem> items, bool includeAll)
    {
        static object Position(CapturePosition position) => new
        {
            sessionId = FormatScenarioId(position.SessionId), receiveSequence = position.Sequence, offsetUtf16 = position.Offset
        };
        var result = new List<object>();
        foreach (var range in ranges)
        {
            // Intersect with the final slices, not their enclosing bounds: exclusions and searches can leave holes.
            var spans = items.Select(item => (Item: item, Bounds: ClipCaptureInterval(item, range)))
                .Where(x => x.Bounds.From < x.Bounds.To)
                .Select(x => new { eventId = $"{FormatScenarioId(x.Item.SessionId)}:{FormatScenarioId(x.Item.Sequence)}", startUtf16 = x.Bounds.From, lengthUtf16 = x.Bounds.To - x.Bounds.From })
                .ToArray();
            if (includeAll || spans.Length > 0)
                result.Add(new { id = FormatScenarioId(range.Id), name = range.Name, start = Position(range.Start), end = Position(range.End), tags = range.Tags, spans });
        }
        return result.ToArray();
    }
}
