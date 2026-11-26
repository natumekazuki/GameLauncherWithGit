using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Data.Sqlite;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed partial class SqliteGameLibraryStore : IScenarioHistoryStore
{
    public async Task<long> GetOrCreateBranchRecordIdAsync(long profileId, string repositoryKey, string branchRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchRef);
        const string heads = "refs/heads/";
        if (!branchRef.StartsWith(heads, StringComparison.Ordinal) || branchRef.Length == heads.Length)
            throw new ArgumentException("ブランチの完全な参照名を指定してください。", nameof(branchRef));

        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        // Serialize lookup and insertion so concurrent callers never create an unused record root.
        await using var transaction = connection.BeginTransaction();
        await RequireCaptureProfileAsync(connection, transaction, profileId);
        await using var find = CaptureCommand(connection, transaction, """
            SELECT RecordId FROM ScenarioBranchHistory WHERE ProfileId=$profile AND RepositoryKey=$repo AND BranchRef=$ref
            """, ("$profile", profileId), ("$repo", repositoryKey), ("$ref", branchRef));
        if (await find.ExecuteScalarAsync() is long existing)
        {
            await transaction.CommitAsync();
            return existing;
        }

        // The child row only allocates a record ID. Its profile data is never read or edited separately.
        await using var allocate = CaptureCommand(connection, transaction, "INSERT INTO CaptureGames(Title) VALUES('') RETURNING Id");
        var recordId = (long)(await allocate.ExecuteScalarAsync())!;
        await using var save = CaptureCommand(connection, transaction, """
            INSERT INTO ScenarioBranchHistory(RecordId,ProfileId,RepositoryKey,BranchRef) VALUES($record,$profile,$repo,$ref)
            """, ("$record", recordId), ("$profile", profileId), ("$repo", repositoryKey), ("$ref", branchRef));
        await save.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return recordId;
    }

    public async Task<IReadOnlyList<ScenarioHistoryScope>> GetScenarioHistoryScopesAsync(long profileId)
    {
        await EnsureInitializedAsync(default);
        await using var connection = await OpenConnectionAsync(default);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await RequireCaptureProfileAsync(connection, transaction, profileId);
        var result = new List<ScenarioHistoryScope> { new(profileId, profileId, null, null) };
        await using var query = CaptureCommand(connection, transaction, """
            SELECT RecordId,RepositoryKey,BranchRef FROM ScenarioBranchHistory WHERE ProfileId=$profile
            ORDER BY RepositoryKey,BranchRef
            """, ("$profile", profileId));
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new(reader.GetInt64(0), profileId, reader.GetString(1), reader.GetString(2)));
        return result;
    }

    private static async Task RequireCaptureProfileAsync(SqliteConnection connection, SqliteTransaction transaction, long profileId)
    {
        await using var query = CaptureCommand(connection, transaction, """
            SELECT Id FROM CaptureGames WHERE Id=$profile
                AND NOT EXISTS(SELECT 1 FROM ScenarioBranchHistory WHERE RecordId=$profile)
            """, ("$profile", profileId));
        if (await query.ExecuteScalarAsync() is null) throw new ArgumentException("抽出設定が見つかりません。", nameof(profileId));
    }

    private static async Task<long> ProfileForRecordAsync(SqliteConnection connection, SqliteTransaction? transaction,
        long recordId, CancellationToken cancellationToken = default)
    {
        await using var query = CaptureCommand(connection, transaction,
            "SELECT ProfileId FROM ScenarioBranchHistory WHERE RecordId=$record", ("$record", recordId));
        return await query.ExecuteScalarAsync(cancellationToken) is long profileId ? profileId : recordId;
    }
}
