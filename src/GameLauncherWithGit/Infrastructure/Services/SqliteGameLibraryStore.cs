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
		_databasePath = Path.Combine(FileSystem.AppDataDirectory, DatabaseFileName);
	}

	public async Task<IReadOnlyList<GameCardItem>> GetAllAsync(CancellationToken cancellationToken = default)
	{
		await EnsureInitializedAsync(cancellationToken);

		await using var connection = await OpenConnectionAsync(cancellationToken);
		await using var command = connection.CreateCommand();
		command.CommandText = """
			SELECT Id, Title, ExecutablePath, RelatedRepositoryPathsJson, LastPlayedAt, Status
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
			SELECT Id, Title, ExecutablePath, RelatedRepositoryPathsJson, LastPlayedAt, Status
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
			    Id, Title, ExecutablePath, RelatedRepositoryPathsJson, LastPlayedAt, Status, CreatedAt, UpdatedAt
			)
			VALUES (
			    $id, $title, $executablePath, $relatedRepositoryPathsJson, $lastPlayedAt, $status, $createdAt, $updatedAt
			)
			ON CONFLICT(Id) DO UPDATE SET
			    Title = excluded.Title,
			    ExecutablePath = excluded.ExecutablePath,
			    RelatedRepositoryPathsJson = excluded.RelatedRepositoryPathsJson,
			    LastPlayedAt = excluded.LastPlayedAt,
			    Status = excluded.Status,
			    UpdatedAt = excluded.UpdatedAt;
			""";

		command.Parameters.AddWithValue("$id", game.Id);
		command.Parameters.AddWithValue("$title", game.Title);
		command.Parameters.AddWithValue("$executablePath", game.ExecutablePath);
		command.Parameters.AddWithValue("$relatedRepositoryPathsJson", SerializeRepositoryPaths(game.RelatedRepositoryPaths));
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
			await using var command = connection.CreateCommand();
			command.CommandText = """
				CREATE TABLE IF NOT EXISTS Games (
				    Id TEXT NOT NULL PRIMARY KEY,
				    Title TEXT NOT NULL,
				    ExecutablePath TEXT NOT NULL,
				    RelatedRepositoryPathsJson TEXT NOT NULL,
				    LastPlayedAt TEXT NULL,
				    Status INTEGER NOT NULL,
				    CreatedAt TEXT NOT NULL,
				    UpdatedAt TEXT NOT NULL
				);
				""";
			await command.ExecuteNonQueryAsync(cancellationToken);

			_isInitialized = true;
			_logger.LogInformation("SQLite game library initialized. path={DatabasePath}", _databasePath);
		}
		finally
		{
			_initializeLock.Release();
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
		var relatedRepositoryPathsJson = reader.GetString(3);
		var lastPlayedAtValue = reader.IsDBNull(4) ? null : reader.GetString(4);
		var statusValue = reader.GetInt32(5);

		return new GameCardItem(
			Id: gameId,
			Title: title,
			ExecutablePath: executablePath,
			RelatedRepositoryPaths: DeserializeRepositoryPaths(relatedRepositoryPathsJson),
			LastPlayedAt: ParseDateTimeOffset(lastPlayedAtValue),
			Status: ParseStatus(statusValue));
	}

	private static IReadOnlyList<string> DeserializeRepositoryPaths(string json)
	{
		try
		{
			var values = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
			return NormalizeRepositoryPaths(values);
		}
		catch (JsonException)
		{
			return Array.Empty<string>();
		}
	}

	private static string SerializeRepositoryPaths(IReadOnlyList<string> repositoryPaths)
	{
		return JsonSerializer.Serialize(NormalizeRepositoryPaths(repositoryPaths), JsonOptions);
	}

	private static IReadOnlyList<string> NormalizeRepositoryPaths(IEnumerable<string> repositoryPaths)
	{
		return repositoryPaths
			.Select(static value => value.Trim())
			.Where(static value => !string.IsNullOrWhiteSpace(value))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
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
