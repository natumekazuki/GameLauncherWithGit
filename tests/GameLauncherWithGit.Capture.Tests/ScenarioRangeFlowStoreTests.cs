using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameLauncherWithGit.Capture.Tests;

public sealed class ScenarioRangeFlowStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Environment.GetEnvironmentVariable("SCENARIO_TEST_ROOT") ?? Path.GetTempPath(),
        "range-flow-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "synthetic.db");
    private SqliteGameLibraryStore _store = default!;
    private static readonly CaptureSource Source = new(1, 2, 3, 0, "synthetic", "body");
    private static readonly DateTimeOffset Time = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Opens only this test's SQLite database.</summary>
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new(NullLogger<SqliteGameLibraryStore>.Instance, Database);
        return Task.CompletedTask;
    }

    /// <summary>Stops the matching worker before cleaning up the test database.</summary>
    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Database, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString());
        SqliteConnection.ClearPool(pool);
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>An unsaved range has the same clipped search, filtering, and exclusive cursor semantics as existing history.</summary>
    [Fact]
    public async Task TemporaryRangeClipsUnicodeBeforeFilteringAndPaging()
    {
        var recordId = await CreateRecordAsync("synthetic");
        const string before = "外側🌱", after = "🌎外側";
        var session = await AppendTraceAsync(recordId, [before + "inside-first", "inside-middle", "inside-last" + after]);
        var originals = await _store.GetGameCaptureItemsAsync(recordId);
        var dialogue = Assert.Single(await _store.GetCaptureLabelsAsync(recordId), item => item.Kind == CaptureKind.Dialogue);
        await _store.SetCaptureLabelsAsync(session, originals.Select(x => x.Id).ToArray(), dialogue.Id);
        await _store.SetCaptureSpeakerAsync(session, originals[0].Id, "Alice");
        await _store.SetCaptureSpeakerAsync(session, originals[1].Id, "Bob");
        await _store.SetCaptureSpeakerAsync(session, originals[2].Id, "Alice");
        var interval = new CaptureRangeSelection(recordId, new(session, 1, before.Length), new(session, 3, "inside-last".Length));

        var last = Assert.Single(await _store.GetGameCaptureItemsAsync(recordId,
            latest: true, limit: 1, textSearch: " INSIDE ", speakerSearch: "alice", kind: CaptureKind.Dialogue, interval: interval));
        Assert.Equal("inside-last", last.Text);
        Assert.Equal(interval.End, last.EndPosition);
        var first = Assert.Single(await _store.GetGameCaptureItemsAsync(recordId,
            latest: true, before: last.StartPosition, limit: 1, labelId: dialogue.Id, textSearch: "inside", speakerSearch: "Alice", interval: interval));
        Assert.Equal("inside-first", first.Text);
        Assert.Equal(originals[0].Id, first.Id);
        Assert.Equal(interval.Start, first.StartPosition);
        Assert.Empty(await _store.GetGameCaptureItemsAsync(recordId, latest: true, before: first.StartPosition, interval: interval));
        Assert.Empty(await _store.GetGameCaptureItemsAsync(recordId, textSearch: "外側", interval: interval));
        Assert.Empty(await _store.GetGameCaptureItemsAsync(recordId, unclassifiedOnly: true, interval: interval));
        Assert.Equal(last.Id, Assert.Single(await _store.GetGameCaptureItemsAsync(recordId,
            limit: 1, offset: 1, speakerSearch: "Alice", interval: interval)).Id);
        Assert.Empty(await _store.GetCaptureRangesAsync(recordId));
    }

    /// <summary>A branch or syntactically invalid bounds cannot be used as a read token for this record.</summary>
    [Fact]
    public async Task TemporaryRangeRejectsForeignReversedAndInvalidPositions()
    {
        var profile = await CreateRecordAsync("synthetic");
        var record = await _store.GetOrCreateBranchRecordIdAsync(profile, "local/synthetic", "refs/heads/current");
        var other = await _store.GetOrCreateBranchRecordIdAsync(profile, "local/synthetic", "refs/heads/other");
        var session = await AppendTraceAsync(record, ["🌱あ"]);
        var otherSession = await AppendTraceAsync(other, ["🌱他"]);
        var start = new CapturePosition(session, 1, 0);
        var end = new CapturePosition(session, 1, 3);
        var legitimate = new CaptureRangeSelection(record, start, end);
        var saved = await _store.SaveCaptureRangeAsync(record, "保存済み", start, end);
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetGameCaptureItemsAsync(record, rangeId: saved, interval: legitimate));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetGameCaptureItemsAsync(other, interval: legitimate));
        foreach (var bad in new[]
        {
            legitimate with { End = end with { SessionId = otherSession } },
            legitimate with { Start = end, End = start },
            legitimate with { End = start },
            legitimate with { Start = start with { Offset = 1 } },
            legitimate with { Start = start with { Offset = -1 } },
            legitimate with { End = end with { Offset = 4 } },
            legitimate with { End = end with { Sequence = 9 } }
        })
            await Assert.ThrowsAsync<ArgumentException>(() => _store.GetGameCaptureItemsAsync(record, interval: bad));
        Assert.Single(await _store.GetGameCaptureItemsAsync(record, interval: legitimate));
        Assert.Single(await _store.GetCaptureRangesAsync(record));
    }

    /// <summary>The real material snapshot exposes enough confirmed correspondence to derive branch and join range edges.</summary>
    [Fact]
    public async Task RealMatchingSnapshotProjectsSavedOpeningAndEndingAcrossTwoVisits()
    {
        var record = await CreateRecordAsync("synthetic");
        string[] opening = ["放課後の教室には夕焼けが差し込んでいた。", "窓辺に立つ彼女を見つめていた。", "言いかけた言葉を飲み込んだ。"];
        string[] ending = ["翌朝の登校。いつもの通学路を歩く。", "校門の前に友人の姿を見つけた。", "教室の扉を開けて挨拶を交わした。"];
        const string choiceA = "教室に残る", choiceB = "美咲を追う";
        var first = await AppendTraceAsync(record, [.. opening, choiceA, .. ending]);
        var second = await AppendTraceAsync(record, [.. opening, choiceB, .. ending]);
        var open = await _store.SaveCaptureRangeAsync(record, "序章", new(first, 1, 0), new(first, 3, opening[^1].Length));
        var left = await _store.SaveCaptureRangeAsync(record, "分岐", new(first, 4, 0), new(first, 4, choiceA.Length));
        var right = await _store.SaveCaptureRangeAsync(record, "分岐", new(second, 4, 0), new(second, 4, choiceB.Length));
        var close = await _store.SaveCaptureRangeAsync(record, "再会", new(first, 5, 0), new(first, 7, ending[^1].Length));
        var snapshot = await _store.GetScenarioMaterialAsync(record, new());
        var projected = ScenarioRangeFlowProjector.Build(snapshot);

        Assert.True(snapshot.MatchingComplete);
        Assert.Single(projected.Charts);
        Assert.Equal(4, projected.Nodes.Count);
        var byRange = projected.Nodes.ToDictionary(n => n.SavedRangeId!.Value, n => n.Id);
        Assert.Equal(new HashSet<ScenarioRangeFlowEdge>
        {
            new(byRange[open], byRange[left]), new(byRange[open], byRange[right]),
            new(byRange[left], byRange[close]), new(byRange[right], byRange[close])
        }, projected.Edges.ToHashSet());
        Assert.Equal(4, (await _store.GetCaptureRangesAsync(record)).Count);
    }

    /// <summary>Registers a synthetic record without launching an executable or opening a repository.</summary>
    private async Task<long> CreateRecordAsync(string gameId)
    {
        await _store.UpsertAsync(new(gameId, "合成作品", "synthetic.exe", null, null, null, GameCardStatus.Unknown));
        return (await _store.GetOrCreateCaptureProfileAsync(gameId)).Id;
    }

    /// <summary>Appends a complete, stopped run using the same successful body-persistence entry point as capture.</summary>
    private async Task<long> AppendTraceAsync(long record, IReadOnlyList<string> texts)
    {
        var session = await _store.BeginCaptureSessionAsync(record);
        for (var index = 0; index < texts.Count; index++)
            await _store.AppendCaptureAsync(session, new(index + 1, Time.AddSeconds(index), Source, texts[index], CaptureRole.Body),
                new() { OutsideIsNarration = true });
        await _store.EndCaptureSessionAsync(session);
        return session;
    }
}
