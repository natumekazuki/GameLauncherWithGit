using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class SqliteGameLibraryStore : IGameLibraryStore, IRepositorySyncHistoryStore, ISaveLinkStore
{
	private const string DatabaseFileName = "game-library.db";
	private const int DefaultMaxHistoryPerRepository = 50;
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly ILogger<SqliteGameLibraryStore> _logger;
	private readonly SemaphoreSlim _initializeLock = new(1, 1);
	private readonly string _databasePath;
	private bool _isInitialized;

	public SqliteGameLibraryStore(ILogger<SqliteGameLibraryStore> logger)
	{
		_logger = logger;
		_databasePath = Path.Combine(FileSystem.AppDataDirectory, DatabaseFileName);
	}

	public async Task<IReadOnlyList<GameCardItem>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);

		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT Id, Title, ExecutablePath, RelatedRepositoryPath, RelatedRepositoryPathsJson, ThumbnailPath, LastPlayedAt, Status, IsPinned
			FROM Games
			ORDER BY
			    IsPinned DESC,
			    CASE WHEN LastPlayedAt IS NULL THEN 1 ELSE 0 END,
			    LastPlayedAt DESC,
			    Title COLLATE NOCASE;
			""";

		var result = new List<GameCardItem>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			result.Add(MapGame(reader));
		}

		return result;
	}

	public async Task<GameCardItem?> FindByIdAsync(string gameId, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);

		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT Id, Title, ExecutablePath, RelatedRepositoryPath, RelatedRepositoryPathsJson, ThumbnailPath, LastPlayedAt, Status, IsPinned
			FROM Games
			WHERE Id = $id
			LIMIT 1;
			""";
		command.Parameters.AddWithValue("$id", gameId);

		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		if (await reader.ReadAsync(cancellationToken))
		{
			return MapGame(reader);
		}

		return null;
	}

	public async Task UpsertAsync(GameCardItem game, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);

		var now = DateTimeOffset.Now.ToString("O");
		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO Games (
			    Id, Title, ExecutablePath, RelatedRepositoryPath, RelatedRepositoryPathsJson, ThumbnailPath, LastPlayedAt, Status, IsPinned, CreatedAt, UpdatedAt
			)
			VALUES (
			    $id, $title, $executablePath, $relatedRepositoryPath, $relatedRepositoryPathsJson, $thumbnailPath, $lastPlayedAt, $status, $isPinned, $createdAt, $updatedAt
			)
			ON CONFLICT(Id) DO UPDATE SET
			    Title = excluded.Title,
			    ExecutablePath = excluded.ExecutablePath,
			    RelatedRepositoryPath = excluded.RelatedRepositoryPath,
			    RelatedRepositoryPathsJson = excluded.RelatedRepositoryPathsJson,
			    ThumbnailPath = excluded.ThumbnailPath,
			    LastPlayedAt = excluded.LastPlayedAt,
			    Status = excluded.Status,
			    IsPinned = excluded.IsPinned,
			    UpdatedAt = excluded.UpdatedAt;
			""";

		var normalizedRepositoryPath = NormalizeSingleRepositoryPath(game.RelatedRepositoryPath);
		command.Parameters.AddWithValue("$id", game.Id);
		command.Parameters.AddWithValue("$title", game.Title);
		command.Parameters.AddWithValue("$executablePath", game.ExecutablePath);
		command.Parameters.AddWithValue("$relatedRepositoryPath", normalizedRepositoryPath ?? (object)DBNull.Value);
		command.Parameters.AddWithValue("$relatedRepositoryPathsJson", SerializeLegacyRepositoryPaths(normalizedRepositoryPath));
		command.Parameters.AddWithValue("$thumbnailPath", NormalizeThumbnailPath(game.ThumbnailPath) ?? (object)DBNull.Value);
		command.Parameters.AddWithValue("$lastPlayedAt", game.LastPlayedAt?.ToString("O") ?? (object)DBNull.Value);
		command.Parameters.AddWithValue("$status", (int)game.Status);
		command.Parameters.AddWithValue("$isPinned", game.IsPinned ? 1 : 0);
		command.Parameters.AddWithValue("$createdAt", now);
		command.Parameters.AddWithValue("$updatedAt", now);

		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task DeleteAsync(string gameId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return;
		}

		await EnsureInitializedAsync(cancellationToken);

		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using (var deleteLinksCommand = connection.CreateCommand())
		{
			deleteLinksCommand.CommandText = """
				DELETE FROM GameSaveLinks
				WHERE GameId = $gameId;
				""";
			deleteLinksCommand.Parameters.AddWithValue("$gameId", gameId.Trim());
			await deleteLinksCommand.ExecuteNonQueryAsync(cancellationToken);
		}

		await using var command = connection.CreateCommand();
		command.CommandText = """
			DELETE FROM Games
			WHERE Id = $id;
			""";
		command.Parameters.AddWithValue("$id", gameId.Trim());
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task<IReadOnlyList<GameSaveLinkItem>> GetByGameIdAsync(
		string gameId,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return Array.Empty<GameSaveLinkItem>();
		}

		await EnsureInitializedAsync(cancellationToken);

		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT Id, GameId, DisplayName, LocalPath, TargetPath, EnsureOnLaunch, OrderNo
			FROM GameSaveLinks
			WHERE GameId = $gameId
			ORDER BY OrderNo ASC, CreatedAt ASC;
			""";
		command.Parameters.AddWithValue("$gameId", gameId.Trim());

		var result = new List<GameSaveLinkItem>();
		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			result.Add(MapSaveLink(reader));
		}

		return result;
	}

	public async Task ReplaceByGameIdAsync(
		string gameId,
		IReadOnlyList<GameSaveLinkItem> links,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return;
		}

		await EnsureInitializedAsync(cancellationToken);

		var normalizedGameId = gameId.Trim();
		var now = DateTimeOffset.UtcNow.ToString("O");

		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
		await using (var deleteCommand = connection.CreateCommand())
		{
			deleteCommand.Transaction = transaction;
			deleteCommand.CommandText = """
				DELETE FROM GameSaveLinks
				WHERE GameId = $gameId;
				""";
			deleteCommand.Parameters.AddWithValue("$gameId", normalizedGameId);
			await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
		}

		if (links is not null)
		{
			foreach (var link in links.OrderBy(static item => item.OrderNo))
			{
				var id = string.IsNullOrWhiteSpace(link.Id)
					? $"save-link-{Guid.NewGuid():N}"
					: link.Id.Trim();
				var displayName = string.IsNullOrWhiteSpace(link.DisplayName)
					? link.LocalPath
					: link.DisplayName.Trim();
				await using var insertCommand = connection.CreateCommand();
				insertCommand.Transaction = transaction;
				insertCommand.CommandText = """
					INSERT INTO GameSaveLinks (
					    Id, GameId, DisplayName, LocalPath, TargetPath, EnsureOnLaunch, OrderNo, CreatedAt, UpdatedAt
					)
					VALUES (
					    $id, $gameId, $displayName, $localPath, $targetPath, $ensureOnLaunch, $orderNo, $createdAt, $updatedAt
					);
					""";
				insertCommand.Parameters.AddWithValue("$id", id);
				insertCommand.Parameters.AddWithValue("$gameId", normalizedGameId);
				insertCommand.Parameters.AddWithValue("$displayName", displayName);
				insertCommand.Parameters.AddWithValue("$localPath", link.LocalPath.Trim());
				insertCommand.Parameters.AddWithValue("$targetPath", link.TargetPath.Trim());
				insertCommand.Parameters.AddWithValue("$ensureOnLaunch", link.EnsureOnLaunch ? 1 : 0);
				insertCommand.Parameters.AddWithValue("$orderNo", Math.Max(0, link.OrderNo));
				insertCommand.Parameters.AddWithValue("$createdAt", now);
				insertCommand.Parameters.AddWithValue("$updatedAt", now);
				await insertCommand.ExecuteNonQueryAsync(cancellationToken);
			}
		}

		await transaction.CommitAsync(cancellationToken);
	}

	public async Task DeleteByGameIdAsync(string gameId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return;
		}

		await EnsureInitializedAsync(cancellationToken);

		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			DELETE FROM GameSaveLinks
			WHERE GameId = $gameId;
			""";
		command.Parameters.AddWithValue("$gameId", gameId.Trim());
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task AppendAsync(RepositorySyncHistoryItem entry, CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);

		if (string.IsNullOrWhiteSpace(entry.RepositoryId))
		{
			return;
		}

		var normalizedRepositoryId = entry.RepositoryId.Trim();
		var startedAt = entry.StartedAt.ToString("O");
		var finishedAt = entry.FinishedAt.ToString("O");
		var durationMs = Math.Max(0L, entry.DurationMs);
		var createdAt = DateTimeOffset.UtcNow.ToString("O");

		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using (var insertCommand = connection.CreateCommand())
		{
			insertCommand.CommandText = """
				INSERT INTO RepositorySyncHistory (
				    RepositoryId, Status, StartedAt, FinishedAt, DurationMs, Command, Reason, CreatedAt
				)
				VALUES (
				    $repositoryId, $status, $startedAt, $finishedAt, $durationMs, $command, $reason, $createdAt
				);
				""";
			insertCommand.Parameters.AddWithValue("$repositoryId", normalizedRepositoryId);
			insertCommand.Parameters.AddWithValue("$status", (int)entry.Status);
			insertCommand.Parameters.AddWithValue("$startedAt", startedAt);
			insertCommand.Parameters.AddWithValue("$finishedAt", finishedAt);
			insertCommand.Parameters.AddWithValue("$durationMs", durationMs);
			insertCommand.Parameters.AddWithValue("$command", entry.Command?.Trim() ?? (object)DBNull.Value);
			insertCommand.Parameters.AddWithValue("$reason", entry.Reason?.Trim() ?? (object)DBNull.Value);
			insertCommand.Parameters.AddWithValue("$createdAt", createdAt);
			await insertCommand.ExecuteNonQueryAsync(cancellationToken);
		}

		await using var pruneCommand = connection.CreateCommand();
		pruneCommand.CommandText = """
			DELETE FROM RepositorySyncHistory
			WHERE RepositoryId = $repositoryId
			  AND Id NOT IN (
			      SELECT Id
			      FROM RepositorySyncHistory
			      WHERE RepositoryId = $repositoryId
			      ORDER BY FinishedAt DESC, Id DESC
			      LIMIT $maxEntries
			  );
			""";
		pruneCommand.Parameters.AddWithValue("$repositoryId", normalizedRepositoryId);
		pruneCommand.Parameters.AddWithValue("$maxEntries", DefaultMaxHistoryPerRepository);
		await pruneCommand.ExecuteNonQueryAsync(cancellationToken);
	}

	public async Task<IReadOnlyDictionary<string, IReadOnlyList<RepositorySyncHistoryItem>>> GetLatestByRepositoryIdsAsync(
		IReadOnlyCollection<string> repositoryIds,
		int limitPerRepository,
		CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);

		if (repositoryIds is null || repositoryIds.Count == 0)
		{
			return new Dictionary<string, IReadOnlyList<RepositorySyncHistoryItem>>(StringComparer.OrdinalIgnoreCase);
		}

		var normalizedLimit = Math.Clamp(limitPerRepository, 1, 20);
		var normalizedRepositoryIds = repositoryIds
			.Where(static id => !string.IsNullOrWhiteSpace(id))
			.Select(static id => id.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (normalizedRepositoryIds.Length == 0)
		{
			return new Dictionary<string, IReadOnlyList<RepositorySyncHistoryItem>>(StringComparer.OrdinalIgnoreCase);
		}

		await using var connection = await OpenConnectionAsync(cancellationToken);
		var result = new Dictionary<string, IReadOnlyList<RepositorySyncHistoryItem>>(StringComparer.OrdinalIgnoreCase);
		foreach (var repositoryId in normalizedRepositoryIds)
		{
			await using var command = connection.CreateCommand();
			command.CommandText = """
				SELECT Id, RepositoryId, Status, StartedAt, FinishedAt, DurationMs, Command, Reason
				FROM RepositorySyncHistory
				WHERE RepositoryId = $repositoryId
				ORDER BY FinishedAt DESC, Id DESC
				LIMIT $limit;
				""";
			command.Parameters.AddWithValue("$repositoryId", repositoryId);
			command.Parameters.AddWithValue("$limit", normalizedLimit);

			var entries = new List<RepositorySyncHistoryItem>(normalizedLimit);
			await using var reader = await command.ExecuteReaderAsync(cancellationToken);
			while (await reader.ReadAsync(cancellationToken))
			{
				entries.Add(MapRepositorySyncHistory(reader));
			}

			result[repositoryId] = entries;
		}

		return result;
	}

	private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
	{
		if (_isInitialized)
		{
			return;
		}

		await _initializeLock.WaitAsync(cancellationToken);
		try
		{
			if (_isInitialized)
			{
				return;
			}

			var directory = Path.GetDirectoryName(_databasePath);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			await using var connection = await OpenConnectionAsync(cancellationToken);
			await EnsureSchemaAsync(connection, cancellationToken);
			await BackfillRepositoryPathAsync(connection, cancellationToken);

			_isInitialized = true;
			_logger.LogInformation("SQLite game library initialized. path={DatabasePath}", _databasePath);
		}
		finally
		{
			_initializeLock.Release();
		}
	}

	private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		await using var createCommand = connection.CreateCommand();
		createCommand.CommandText = """
			CREATE TABLE IF NOT EXISTS Games (
			    Id TEXT NOT NULL PRIMARY KEY,
			    Title TEXT NOT NULL,
			    ExecutablePath TEXT NOT NULL,
			    RelatedRepositoryPath TEXT NULL,
			    RelatedRepositoryPathsJson TEXT NULL,
			    ThumbnailPath TEXT NULL,
			    LastPlayedAt TEXT NULL,
			    Status INTEGER NOT NULL,
			    IsPinned INTEGER NOT NULL DEFAULT 0,
			    CreatedAt TEXT NOT NULL,
			    UpdatedAt TEXT NOT NULL
			);
			""";
		await createCommand.ExecuteNonQueryAsync(cancellationToken);

		await EnsureColumnExistsAsync(connection, "RelatedRepositoryPath", "TEXT NULL", cancellationToken);
		await EnsureColumnExistsAsync(connection, "RelatedRepositoryPathsJson", "TEXT NULL", cancellationToken);
		await EnsureColumnExistsAsync(connection, "ThumbnailPath", "TEXT NULL", cancellationToken);
		await EnsureColumnExistsAsync(connection, "IsPinned", "INTEGER NOT NULL DEFAULT 0", cancellationToken);

		await using var createSaveLinksTableCommand = connection.CreateCommand();
		createSaveLinksTableCommand.CommandText = """
			CREATE TABLE IF NOT EXISTS GameSaveLinks (
			    Id TEXT NOT NULL PRIMARY KEY,
			    GameId TEXT NOT NULL,
			    DisplayName TEXT NOT NULL,
			    LocalPath TEXT NOT NULL,
			    TargetPath TEXT NOT NULL,
			    EnsureOnLaunch INTEGER NOT NULL DEFAULT 1,
			    OrderNo INTEGER NOT NULL DEFAULT 0,
			    CreatedAt TEXT NOT NULL,
			    UpdatedAt TEXT NOT NULL
			);
			""";
		await createSaveLinksTableCommand.ExecuteNonQueryAsync(cancellationToken);

		await using var createSaveLinksGameIdIndexCommand = connection.CreateCommand();
		createSaveLinksGameIdIndexCommand.CommandText = """
			CREATE INDEX IF NOT EXISTS IX_GameSaveLinks_GameId_OrderNo
			ON GameSaveLinks (GameId, OrderNo ASC);
			""";
		await createSaveLinksGameIdIndexCommand.ExecuteNonQueryAsync(cancellationToken);

		await using var createHistoryTableCommand = connection.CreateCommand();
		createHistoryTableCommand.CommandText = """
			CREATE TABLE IF NOT EXISTS RepositorySyncHistory (
			    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
			    RepositoryId TEXT NOT NULL,
			    Status INTEGER NOT NULL,
			    StartedAt TEXT NOT NULL,
			    FinishedAt TEXT NOT NULL,
			    DurationMs INTEGER NOT NULL,
			    Command TEXT NULL,
			    Reason TEXT NULL,
			    CreatedAt TEXT NOT NULL
			);
			""";
		await createHistoryTableCommand.ExecuteNonQueryAsync(cancellationToken);

		await using var createHistoryIndexCommand = connection.CreateCommand();
		createHistoryIndexCommand.CommandText = """
			CREATE INDEX IF NOT EXISTS IX_RepositorySyncHistory_RepositoryId_FinishedAt
			ON RepositorySyncHistory (RepositoryId, FinishedAt DESC);
			""";
		await createHistoryIndexCommand.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task EnsureColumnExistsAsync(
		SqliteConnection connection,
		string columnName,
		string columnType,
		CancellationToken cancellationToken)
	{
		await using var tableInfoCommand = connection.CreateCommand();
		tableInfoCommand.CommandText = "PRAGMA table_info(Games);";
		await using var reader = await tableInfoCommand.ExecuteReaderAsync(cancellationToken);
		while (await reader.ReadAsync(cancellationToken))
		{
			var existingColumnName = reader.GetString(1);
			if (string.Equals(existingColumnName, columnName, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}

		await using var alterCommand = connection.CreateCommand();
		alterCommand.CommandText = $"ALTER TABLE Games ADD COLUMN {columnName} {columnType};";
		await alterCommand.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task BackfillRepositoryPathAsync(SqliteConnection connection, CancellationToken cancellationToken)
	{
		await using var selectCommand = connection.CreateCommand();
		selectCommand.CommandText = """
			SELECT Id, RelatedRepositoryPath, RelatedRepositoryPathsJson
			FROM Games;
			""";

		var updates = new List<(string Id, string RepositoryPath)>();
		await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
		{
			while (await reader.ReadAsync(cancellationToken))
			{
				var id = reader.GetString(0);
				var repositoryPath = reader.IsDBNull(1) ? null : reader.GetString(1);
				if (!string.IsNullOrWhiteSpace(repositoryPath))
				{
					continue;
				}

				var legacyJson = reader.IsDBNull(2) ? null : reader.GetString(2);
				var migrated = DeserializeRepositoryPathLegacy(legacyJson);
				if (!string.IsNullOrWhiteSpace(migrated))
				{
					updates.Add((id, migrated));
				}
			}
		}

		foreach (var update in updates)
		{
			await using var updateCommand = connection.CreateCommand();
			updateCommand.CommandText = """
				UPDATE Games
				SET RelatedRepositoryPath = $relatedRepositoryPath,
				    RelatedRepositoryPathsJson = $relatedRepositoryPathsJson
				WHERE Id = $id;
				""";
			updateCommand.Parameters.AddWithValue("$id", update.Id);
			updateCommand.Parameters.AddWithValue("$relatedRepositoryPath", update.RepositoryPath);
			updateCommand.Parameters.AddWithValue("$relatedRepositoryPathsJson", SerializeLegacyRepositoryPaths(update.RepositoryPath));
			await updateCommand.ExecuteNonQueryAsync(cancellationToken);
		}
	}

	private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
	{
		var builder = new SqliteConnectionStringBuilder
		{
			DataSource = _databasePath,
			Mode = SqliteOpenMode.ReadWriteCreate
		};

		var connection = new SqliteConnection(builder.ConnectionString);
		await connection.OpenAsync(cancellationToken);
		return connection;
	}

	private static GameCardItem MapGame(SqliteDataReader reader)
	{
		var gameId = reader.GetString(0);
		var title = reader.GetString(1);
		var executablePath = reader.GetString(2);
		var relatedRepositoryPath = reader.IsDBNull(3) ? null : reader.GetString(3);
		var relatedRepositoryPathsJson = reader.IsDBNull(4) ? null : reader.GetString(4);
		var thumbnailPath = reader.IsDBNull(5) ? null : reader.GetString(5);
		var lastPlayedAtValue = reader.IsDBNull(6) ? null : reader.GetString(6);
		var statusValue = reader.GetInt32(7);
		var isPinned = !reader.IsDBNull(8) && reader.GetInt32(8) != 0;

		return new GameCardItem(
			Id: gameId,
			Title: title,
			ExecutablePath: executablePath,
			RelatedRepositoryPath: NormalizeSingleRepositoryPath(relatedRepositoryPath) ?? DeserializeRepositoryPathLegacy(relatedRepositoryPathsJson),
			ThumbnailPath: NormalizeThumbnailPath(thumbnailPath),
			LastPlayedAt: ParseDateTimeOffset(lastPlayedAtValue),
			Status: ParseStatus(statusValue),
			IsPinned: isPinned);
	}

	private static string? DeserializeRepositoryPathLegacy(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		try
		{
			var values = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
			return NormalizeSingleRepositoryPath(values.FirstOrDefault());
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string SerializeLegacyRepositoryPaths(string? repositoryPath)
	{
		if (string.IsNullOrWhiteSpace(repositoryPath))
		{
			return "[]";
		}

		return JsonSerializer.Serialize(new[] { repositoryPath }, JsonOptions);
	}

	private static string? NormalizeSingleRepositoryPath(string? repositoryPath)
	{
		if (string.IsNullOrWhiteSpace(repositoryPath))
		{
			return null;
		}

		return repositoryPath.Trim();
	}

	private static string? NormalizeThumbnailPath(string? thumbnailPath)
	{
		if (string.IsNullOrWhiteSpace(thumbnailPath))
		{
			return null;
		}

		return thumbnailPath.Trim();
	}

	private static DateTimeOffset? ParseDateTimeOffset(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		if (DateTimeOffset.TryParse(value, out var parsed))
		{
			return parsed;
		}

		return null;
	}

	private static GameCardStatus ParseStatus(int value)
	{
		return Enum.IsDefined(typeof(GameCardStatus), value)
			? (GameCardStatus)value
			: GameCardStatus.Unknown;
	}

	private static GameSaveLinkItem MapSaveLink(SqliteDataReader reader)
	{
		var id = reader.GetString(0);
		var gameId = reader.GetString(1);
		var displayName = reader.GetString(2);
		var localPath = reader.GetString(3);
		var targetPath = reader.GetString(4);
		var ensureOnLaunch = !reader.IsDBNull(5) && reader.GetInt32(5) != 0;
		var orderNo = !reader.IsDBNull(6) ? Math.Max(0, reader.GetInt32(6)) : 0;

		return new GameSaveLinkItem(
			Id: id,
			GameId: gameId,
			DisplayName: displayName,
			LocalPath: localPath,
			TargetPath: targetPath,
			OrderNo: orderNo,
			EnsureOnLaunch: ensureOnLaunch);
	}

	private static RepositorySyncHistoryItem MapRepositorySyncHistory(SqliteDataReader reader)
	{
		var id = reader.GetInt64(0);
		var repositoryId = reader.GetString(1);
		var statusValue = reader.GetInt32(2);
		var startedAt = ParseDateTimeOffset(reader.IsDBNull(3) ? null : reader.GetString(3)) ?? DateTimeOffset.MinValue;
		var finishedAt = ParseDateTimeOffset(reader.IsDBNull(4) ? null : reader.GetString(4)) ?? startedAt;
		var durationMs = !reader.IsDBNull(5) ? Math.Max(0L, reader.GetInt64(5)) : Math.Max(0L, (long)(finishedAt - startedAt).TotalMilliseconds);
		var command = reader.IsDBNull(6) ? null : reader.GetString(6);
		var reason = reader.IsDBNull(7) ? null : reader.GetString(7);

		var status = Enum.IsDefined(typeof(RepositorySyncHistoryStatus), statusValue)
			? (RepositorySyncHistoryStatus)statusValue
			: RepositorySyncHistoryStatus.Failed;
		return new RepositorySyncHistoryItem(
			Id: id,
			RepositoryId: repositoryId,
			Status: status,
			StartedAt: startedAt,
			FinishedAt: finishedAt,
			DurationMs: durationMs,
			Command: command,
			Reason: reason);
	}
}
