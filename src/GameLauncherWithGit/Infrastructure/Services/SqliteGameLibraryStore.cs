using GameLauncherWithGit;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class SqliteGameLibraryStore : IGameLibraryStore
{
	private const string DatabaseFileName = "game-library.db";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly ILogger<SqliteGameLibraryStore> _logger;
	private readonly SemaphoreSlim _initializeLock = new(1, 1);
	private readonly string _databasePath;
	private bool _isInitialized;

	public SqliteGameLibraryStore(ILogger<SqliteGameLibraryStore> logger)
	{
		_logger = logger;
		_databasePath = Path.Combine(AppDataPaths.BaseDirectory, DatabaseFileName);
	}

	public async Task<IReadOnlyList<GameCardItem>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);

		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT Id, Title, ExecutablePath, RelatedRepositoryPath, RelatedRepositoryPathsJson, ThumbnailPath, LastPlayedAt, Status
			FROM Games
			ORDER BY
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
			SELECT Id, Title, ExecutablePath, RelatedRepositoryPath, RelatedRepositoryPathsJson, ThumbnailPath, LastPlayedAt, Status
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
			    Id, Title, ExecutablePath, RelatedRepositoryPath, RelatedRepositoryPathsJson, ThumbnailPath, LastPlayedAt, Status, CreatedAt, UpdatedAt
			)
			VALUES (
			    $id, $title, $executablePath, $relatedRepositoryPath, $relatedRepositoryPathsJson, $thumbnailPath, $lastPlayedAt, $status, $createdAt, $updatedAt
			)
			ON CONFLICT(Id) DO UPDATE SET
			    Title = excluded.Title,
			    ExecutablePath = excluded.ExecutablePath,
			    RelatedRepositoryPath = excluded.RelatedRepositoryPath,
			    RelatedRepositoryPathsJson = excluded.RelatedRepositoryPathsJson,
			    ThumbnailPath = excluded.ThumbnailPath,
			    LastPlayedAt = excluded.LastPlayedAt,
			    Status = excluded.Status,
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
		command.Parameters.AddWithValue("$createdAt", now);
		command.Parameters.AddWithValue("$updatedAt", now);

		await command.ExecuteNonQueryAsync(cancellationToken);
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
			    CreatedAt TEXT NOT NULL,
			    UpdatedAt TEXT NOT NULL
			);
			""";
		await createCommand.ExecuteNonQueryAsync(cancellationToken);

		await EnsureColumnExistsAsync(connection, "RelatedRepositoryPath", "TEXT NULL", cancellationToken);
		await EnsureColumnExistsAsync(connection, "RelatedRepositoryPathsJson", "TEXT NULL", cancellationToken);
		await EnsureColumnExistsAsync(connection, "ThumbnailPath", "TEXT NULL", cancellationToken);
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

		return new GameCardItem(
			Id: gameId,
			Title: title,
			ExecutablePath: executablePath,
			RelatedRepositoryPath: NormalizeSingleRepositoryPath(relatedRepositoryPath) ?? DeserializeRepositoryPathLegacy(relatedRepositoryPathsJson),
			ThumbnailPath: NormalizeThumbnailPath(thumbnailPath),
			LastPlayedAt: ParseDateTimeOffset(lastPlayedAtValue),
			Status: ParseStatus(statusValue));
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
}
