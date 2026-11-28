using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

/// <summary>Exercises row deletion through the real SQLite store without the desktop host or real capture.</summary>
public sealed class ScenarioOccurrenceDeletionTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(), "occurrence-delete-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "synthetic.db");
    private SqliteGameLibraryStore _store = default!;
    private static readonly CaptureSource Source = new(1, 2, 3, 0, "synthetic", "body");
    private static readonly DateTimeOffset Time = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l1sAAAAASUVORK5CYII=");

    /// <summary>Isolates each test database and its screenshot directory.</summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Database);
        return Task.CompletedTask;
    }

    /// <summary>Shuts down matching work before discarding the dedicated synthetic database and files.</summary>
    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        using var pooled = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Database, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString());
        SqliteConnection.ClearPool(pooled);
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Clipped rows resolve by ID and coordinates; duplicate and hidden siblings retain their own rows.</summary>
    [Fact]
    public async Task ClippedRowDeletesOnlyItsOwnOccurrenceFromSearchFlowAndExport()
    {
        var record = await CreateProfileAsync("game");
        var session = await _store.BeginCaptureSessionAsync(record);
        const string prefix = "「おはよう🌟」", same = "「同文」", suffix = "「末尾」";
        await AppendAsync(session, 1, prefix + same + same + suffix);
        await AppendAsync(session, 2, same);
        await _store.EndCaptureSessionAsync(session);
        var all = await _store.GetCaptureItemsAsync(session);
        Assert.Equal(new[] { prefix, same, same, suffix, same }, all.Select(x => x.Text));
        var selected = all[1];
        var range = await _store.SaveCaptureRangeAsync(record, "一部だけ表示",
            selected.StartPosition with { Offset = selected.Start + 1 },
            selected.EndPosition with { Offset = selected.EndPosition.Offset - 1 });
        var displayed = Assert.Single(await _store.GetGameCaptureItemsAsync(record, rangeId: range, textSearch: "同文"));
        Assert.Equal("同文", displayed.Text);
        Assert.Equal(selected.Id, displayed.Id);
        var before = await _store.GetScenarioMaterialAsync(record, new());
        var confirmed = await ConfirmAsync(record, displayed);
        Assert.Equal(selected, confirmed.Item);

        await _store.DeleteCaptureOccurrenceAsync(record, confirmed);

        var kept = new[] { all[0], all[2], all[3], all[4] };
        Assert.Equal(kept, await _store.GetCaptureItemsAsync(session));
        Assert.Equal(new[] { all[2].Id, all[4].Id },
            (await _store.GetGameCaptureItemsAsync(record, textSearch: "同文")).Select(x => x.Id));
        Assert.Empty(await _store.GetGameCaptureItemsAsync(record, rangeId: range));
        Assert.Null(await _store.FindCaptureItemAtAsync(record, selected.StartPosition));
        Assert.Equal(kept, await _store.GetCaptureExportItemsAsync(record, new()));
        Assert.Equal(new CaptureHistoryCounts(4, 2, 1), await _store.GetCaptureHistoryCountsAsync(record));
        var material = await _store.GetScenarioMaterialAsync(record, new());
        Assert.True(material.Version.Revision > before.Version.Revision);
        Assert.DoesNotContain(material.Occurrences, x => x.Source.ReceiveSequence == selected.Sequence && x.Source.StartUtf16 == selected.Start);
        Assert.Contains(material.Graph.Occurrences, x => x.Start == all[2].StartPosition);
        Assert.DoesNotContain(material.Graph.Occurrences, x => x.Start == selected.StartPosition);

        var zipPath = Path.Combine(_directory, "output.zip");
        await _store.ExportScenarioArchiveAsync(record, new(), zipPath, ScenarioMaterialAccessLevel.Full);
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry("texts.jsonl")!.Open());
        using var firstEvent = JsonDocument.Parse((await reader.ReadLineAsync())!);
        Assert.Equal(new[] { all[0].Start, all[2].Start, all[3].Start },
            firstEvent.RootElement.GetProperty("segments").EnumerateArray().Select(x => x.GetProperty("startUtf16").GetInt32()));
    }

    /// <summary>Rebuilding items after reopening cannot synthesize deleted UTF-16 spans; whole-source deletion still works separately.</summary>
    [Fact]
    public async Task ReopenReapplyAndManualResplitDoNotRestoreDeletedSpan()
    {
        var record = await CreateProfileAsync("game");
        var session = await _store.BeginCaptureSessionAsync(record);
        const string prefix = "「前🌟」", removed = "「削除対象」", suffix = "「後」";
        var raw = prefix + removed + suffix;
        await AppendAsync(session, 1, raw);
        await _store.EndCaptureSessionAsync(session);
        var original = await _store.GetCaptureItemsAsync(session);
        await _store.DeleteCaptureOccurrenceAsync(record, await ConfirmAsync(record, original[1]));

        await using var reopened = new SqliteGameLibraryStore(NullLogger<SqliteGameLibraryStore>.Instance, Database);
        var remaining = await reopened.GetCaptureItemsAsync(session);
        Assert.Equal(new[] { prefix, suffix }, remaining.Select(x => x.Text));
        await reopened.ReapplyCaptureRulesAsync(session, remaining.Select(x => x.Id).ToArray(), new() { SplitDialogue = false });
        var reapplied = await reopened.GetCaptureItemsAsync(session);
        Assert.Equal(new[] { (0, prefix.Length), (prefix.Length + removed.Length, suffix.Length) },
            reapplied.Select(x => (x.Start, x.Length)));
        Assert.Equal(new[] { prefix, suffix }, reapplied.Select(x => x.Text));
        Assert.Empty(await reopened.GetGameCaptureItemsAsync(record, textSearch: "削除対象"));

        // A new split inside the removed span cannot produce a row or assign its metadata to a sibling.
        await reopened.SplitCaptureEventAsync(session, 1, [prefix.Length + 1, prefix.Length + 2],
            [new(null, "前話者"), new(null, "削除部分"), new(null, "後話者")]);
        var resplit = await reopened.GetCaptureItemsAsync(session);
        Assert.Equal(new[] { prefix, suffix }, resplit.Select(x => x.Text));
        Assert.Equal(new[] { "前話者", "後話者" }, resplit.Select(x => x.Speaker));
        Assert.False(await reopened.AppendCaptureAsync(session, new(1, Time, Source, raw, CaptureRole.Body), new()));
        await reopened.DeleteCaptureEventsAsync(record, [new(session, 1)]);
        Assert.Equal(new CaptureHistoryCounts(0, 0, 0), await reopened.GetCaptureHistoryCountsAsync(record));
    }

    /// <summary>Wrong roots and tokens are rejected, including a re-split that recreated exactly the same text at the same offsets.</summary>
    [Fact]
    public async Task CannotDeleteAcrossRecordBranchesOrFromAStaleConfirmation()
    {
        var profile = await CreateProfileAsync("game");
        var other = await CreateProfileAsync("other");
        var record = await _store.GetOrCreateBranchRecordIdAsync(profile, "synthetic/repo", "refs/heads/current");
        var branch = await _store.GetOrCreateBranchRecordIdAsync(profile, "synthetic/repo", "refs/heads/archived");
        var session = await _store.BeginCaptureSessionAsync(record);
        var branchSession = await _store.BeginCaptureSessionAsync(branch);
        var otherSession = await _store.BeginCaptureSessionAsync(other);
        const string same = "「同文」";
        await AppendAsync(session, 1, same + same);
        await AppendAsync(branchSession, 1, same);
        await AppendAsync(otherSession, 1, same);
        await _store.EndCaptureSessionAsync(session);
        var rows = await _store.GetCaptureItemsAsync(session);
        var selected = rows[1];
        var confirmed = await ConfirmAsync(record, selected);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(branch, selected));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteCaptureOccurrenceAsync(branch, confirmed));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteCaptureOccurrenceAsync(other, confirmed with { RecordId = other }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.GetCaptureOccurrenceDeletionTargetAsync(record,
            new(selected.Id, rows[0].StartPosition, selected.EndPosition)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.GetCaptureOccurrenceDeletionTargetAsync(record,
            new(selected.Id, selected.StartPosition with { SessionId = branchSession }, selected.EndPosition)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteCaptureOccurrenceAsync(record,
            confirmed with { Item = confirmed.Item with { Length = confirmed.Item.Length - 1 } }));

        await _store.SplitCaptureEventAsync(session, 1, [same.Length]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteCaptureOccurrenceAsync(record, confirmed));
        var refreshed = (await _store.GetCaptureItemsAsync(session))[1];
        Assert.NotEqual(selected.Id, refreshed.Id);
        Assert.Equal(selected.StartPosition, refreshed.StartPosition);
        Assert.Equal(selected.Text, refreshed.Text);
        await _store.DeleteCaptureOccurrenceAsync(record, await ConfirmAsync(record, refreshed));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConfirmAsync(record, refreshed));
        Assert.Equal(same, Assert.Single(await _store.GetCaptureItemsAsync(session)).Text);
        Assert.Equal(same, Assert.Single(await _store.GetCaptureItemsAsync(branchSession)).Text);
        Assert.Equal(same, Assert.Single(await _store.GetCaptureItemsAsync(otherSession)).Text);
    }

    /// <summary>Attachments stay with surviving siblings and shared files survive deletion of another event's last row.</summary>
    [Fact]
    public async Task LastVisibleOccurrenceDetachesOnlyItsEventAndRejectsStaleImageReads()
    {
        var record = await CreateProfileAsync("game");
        var other = await CreateProfileAsync("other");
        var session = await _store.BeginCaptureSessionAsync(record);
        var otherSession = await _store.BeginCaptureSessionAsync(other);
        await AppendAsync(session, 1, "「前」「後」");
        await AppendAsync(otherSession, 1, "「別」");
        await _store.EndCaptureSessionAsync(session);
        var key = new CaptureEventKey(session, 1);
        var otherKey = new CaptureEventKey(otherSession, 1);
        var shared = new ScreenshotImageData(1, 1, new string('a', 64), Png);
        var commonFile = await _store.SaveScreenshotAsync(new(record, key), shared, Time);
        await _store.SaveScreenshotAsync(new(other, otherKey), shared, Time);
        var exclusiveFile = await _store.SaveScreenshotAsync(new(record, key), shared with { PixelHash = new string('b', 64) }, Time);
        var rows = await _store.GetCaptureItemsAsync(session);

        var first = await _store.DeleteCaptureOccurrenceAsync(record, await ConfirmAsync(record, rows[1]));
        Assert.Equal(0, first.ScreenshotCleanup.RemainingImageCount);
        Assert.Equal(2, (await _store.GetScreenshotCountsAsync(record, [key]))[key]);
        Assert.Equal(Png, await _store.ReadEventScreenshotPngAsync(record, key, exclusiveFile.ImageId));
        var last = await _store.DeleteCaptureOccurrenceAsync(record, await ConfirmAsync(record, rows[0]));
        Assert.Equal(0, last.ScreenshotCleanup.RemainingImageCount);
        Assert.False(File.Exists(exclusiveFile.FilePath));
        Assert.Equal(Png, await _store.ReadEventScreenshotPngAsync(other, otherKey, commonFile.ImageId));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetEventScreenshotsAsync(record, key));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetScreenshotCountsAsync(record, [key]));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetEventScreenshotMetadataAsync(record, [key]));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveScreenshotAsync(new(record, key), shared, Time));
        await _store.SplitCaptureEventAsync(session, 1, []);
        Assert.Empty(await _store.GetCaptureItemsAsync(session));
    }

    /// <summary>A failed SQL delete rolls back both the item and the excluded span rather than suppressing it on the next rebuild.</summary>
    [Fact]
    public async Task SqlFailureCannotCommitAnExclusionWithoutDeletingItsItem()
    {
        var record = await CreateProfileAsync("game");
        var session = await _store.BeginCaptureSessionAsync(record);
        await AppendAsync(session, 1, "「失敗なら残る」");
        await _store.EndCaptureSessionAsync(session);
        var item = Assert.Single(await _store.GetCaptureItemsAsync(session));
        var target = await ConfirmAsync(record, item);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var fault = connection.CreateCommand();
        fault.CommandText = "CREATE TRIGGER TestDeleteFault BEFORE DELETE ON CaptureItems BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;";
        await fault.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqliteException>(() => _store.DeleteCaptureOccurrenceAsync(record, target));
        Assert.Equal(item, Assert.Single(await _store.GetCaptureItemsAsync(session)));
        fault.CommandText = "DROP TRIGGER TestDeleteFault;";
        await fault.ExecuteNonQueryAsync();

        await _store.SplitCaptureEventAsync(session, 1, []);
        Assert.Equal(item.Text, Assert.Single(await _store.GetCaptureItemsAsync(session)).Text);
    }

    /// <summary>A filesystem cleanup failure is returned after SQL commit and remains retryable without restoring the item.</summary>
    [Fact]
    public async Task FileCleanupFailureReportsAnOrphanWithoutRestoringTheOccurrence()
    {
        var record = await CreateProfileAsync("game");
        var session = await _store.BeginCaptureSessionAsync(record);
        await AppendAsync(session, 1, "「削除と回収を分ける」");
        await _store.EndCaptureSessionAsync(session);
        var key = new CaptureEventKey(session, 1);
        var image = await _store.SaveScreenshotAsync(new(record, key), new(1, 1, new string('a', 64), Png), Time);
        var target = await ConfirmAsync(record, Assert.Single(await _store.GetCaptureItemsAsync(session)));

        // A synthetic directory at the expected file path makes File.Delete fail without touching a user file.
        var backup = image.FilePath + ".fixture";
        File.Move(image.FilePath, backup);
        Directory.CreateDirectory(image.FilePath);
        try
        {
            var result = await _store.DeleteCaptureOccurrenceAsync(record, target);
            Assert.Equal(1, result.ScreenshotCleanup.RemainingImageCount);
            Assert.Empty(await _store.GetCaptureItemsAsync(session));
        }
        finally
        {
            Directory.Delete(image.FilePath);
            File.Move(backup, image.FilePath);
        }
        Assert.Equal(0, (await _store.CleanupUnreferencedScreenshotsAsync()).RemainingImageCount);
        Assert.False(File.Exists(image.FilePath));
        Assert.Empty(await _store.GetCaptureItemsAsync(session));
    }

    /// <summary>Creates an isolated record root without linking any real executable or repository.</summary>
    private async Task<long> CreateProfileAsync(string id)
    {
        await _store.UpsertAsync(new(id, id, "synthetic.exe", null, null, null, GameCardStatus.Unknown));
        return (await _store.GetOrCreateCaptureProfileAsync(id)).Id;
    }

    /// <summary>Records one artificial received body event with stable coordinates.</summary>
    private Task<bool> AppendAsync(long session, long sequence, string text) =>
        _store.AppendCaptureAsync(session, new(sequence, Time.AddSeconds(sequence), Source, text, CaptureRole.Body), new());

    /// <summary>Uses the actual displayed row identity to request confirmation.</summary>
    private Task<CaptureOccurrenceDeletionTarget> ConfirmAsync(long record, CaptureTextItem displayed) =>
        _store.GetCaptureOccurrenceDeletionTargetAsync(record, new(displayed.Id, displayed.StartPosition, displayed.EndPosition));
}
