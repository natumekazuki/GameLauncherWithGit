using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using MonochromeMemory.Log.Core;
using MonochromeMemory.Log.Sinks.File;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class LogAccessService : ILogAccessService
{
	private const string LogsDirectoryName = "logs";
	private const string StructuredLogFileName = "app-events.jsonl";
	private const string StructuredLogArchivePattern = "app-events-*.jsonl";
	private const string ServiceName = "GameLauncherWithGit";
	private static readonly JsonSerializerOptions DisplayJsonOptions = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private readonly IAppSettingsService _appSettingsService;
	private readonly ILogDispatcher _logDispatcher;
	private readonly ILogger<LogAccessService> _logger;
	private readonly string _logsDirectoryPath;
	private readonly string _structuredLogPath;
	private readonly ILogStoreReader _logStoreReader;
	private readonly IReadOnlyDictionary<string, object?> _resource;

	public LogAccessService(
		IAppSettingsService appSettingsService,
		ILogDispatcher logDispatcher,
		ILogger<LogAccessService> logger)
	{
		_appSettingsService = appSettingsService;
		_logDispatcher = logDispatcher;
		_logger = logger;
		_logsDirectoryPath = Path.Combine(FileSystem.AppDataDirectory, LogsDirectoryName);
		_structuredLogPath = Path.Combine(_logsDirectoryPath, StructuredLogFileName);
		_logStoreReader = new FileLogStoreReader(_structuredLogPath);
		_resource = BuildResource();
	}

	public async Task AppendErrorAsync(string message, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		EnsureLogsDirectory();
		var normalizedMessage = message.Trim();
		var timestamp = DateTimeOffset.UtcNow;
		var traceContext = CreateTraceContext();
		var payload = new ExceptionLogData(
			Type: "ApplicationError",
			Message: normalizedMessage,
			StackTrace: string.Empty,
			Inner: null,
			HResult: null,
			KeyValues: new Dictionary<string, object?>
			{
				["localTimestamp"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
				["source"] = nameof(LogAccessService)
			});
		var logEvent = new ExceptionLogEvent(
			Trace: traceContext,
			Timestamp: timestamp,
			Resource: _resource,
			Data: payload);
		await _logDispatcher.SendAsync(logEvent, cancellationToken);
	}

	public Task MaintainLogFilesAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureLogsDirectory();

		var settings = _appSettingsService.Get().Normalize();
		DeleteExpiredLogs(settings.LogRetentionDays, cancellationToken);
		RotateStructuredLogIfNeeded(settings.LogMaxFileSizeMb, cancellationToken);
		return Task.CompletedTask;
	}

	public async Task<IReadOnlyList<LogViewerEntry>> GetLatestEntriesAsync(
		int limit,
		LogLevel? severity = null,
		string? keyword = null,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var normalizedLimit = Math.Clamp(limit, 1, 500);
		var normalizedKeyword = NormalizeKeyword(keyword);
		var query = new LogQuery(
			From: DateTimeOffset.MinValue,
			To: DateTimeOffset.MaxValue,
			Severities: severity is null ? null : [severity.Value]);
		var latestEntries = new Queue<LogViewerEntry>(normalizedLimit);

		await foreach (var record in _logStoreReader.QueryLogsAsync(query, cancellationToken))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var entry = ConvertRecord(record);
			if (!MatchesKeyword(entry, normalizedKeyword))
			{
				continue;
			}

			if (latestEntries.Count == normalizedLimit)
			{
				latestEntries.Dequeue();
			}

			latestEntries.Enqueue(entry);
		}

		return latestEntries.Reverse().ToArray();
	}

	public Task OpenLatestErrorLogAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureLogsDirectory();

		if (!File.Exists(_structuredLogPath))
		{
			File.WriteAllText(_structuredLogPath, string.Empty, Encoding.UTF8);
		}

		OpenPath(_structuredLogPath);
		return Task.CompletedTask;
	}

	public Task OpenLogDirectoryAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureLogsDirectory();
		OpenPath(_logsDirectoryPath);
		return Task.CompletedTask;
	}

	private void EnsureLogsDirectory()
	{
		Directory.CreateDirectory(_logsDirectoryPath);
	}

	private static TraceContext CreateTraceContext()
	{
		var traceId = Guid.NewGuid().ToString("N");
		return new TraceContext(
			TraceId: traceId,
			SpanId: traceId[..16],
			ParentSpanId: null,
			TraceFlags: 1,
			TraceState: null);
	}

	private static IReadOnlyDictionary<string, object?> BuildResource()
	{
		return new Dictionary<string, object?>
		{
			[ResourceKeys.ServiceName] = ServiceName,
			[ResourceKeys.DeploymentEnvironment] = "local",
			[ResourceKeys.HostName] = Environment.MachineName,
			[ResourceKeys.ProcessId] = Environment.ProcessId
		};
	}

	private void DeleteExpiredLogs(int retentionDays, CancellationToken cancellationToken)
	{
		if (retentionDays <= 0)
		{
			return;
		}

		var thresholdUtc = DateTimeOffset.UtcNow.AddDays(-retentionDays).UtcDateTime;
		var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (File.Exists(_structuredLogPath))
		{
			targets.Add(_structuredLogPath);
		}

		foreach (var path in Directory.EnumerateFiles(_logsDirectoryPath, StructuredLogArchivePattern, SearchOption.TopDirectoryOnly))
		{
			targets.Add(path);
		}

		foreach (var path in targets)
		{
			cancellationToken.ThrowIfCancellationRequested();

			try
			{
				var lastWrite = File.GetLastWriteTimeUtc(path);
				if (lastWrite >= thresholdUtc)
				{
					continue;
				}

				File.Delete(path);
			}
			catch (FileNotFoundException)
			{
				// 別スレッドで削除済みなら無視する。
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to delete expired log file. path={Path}", path);
			}
		}
	}

	private void RotateStructuredLogIfNeeded(int maxFileSizeMb, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (maxFileSizeMb <= 0 || !File.Exists(_structuredLogPath))
		{
			return;
		}

		long maxBytes;
		try
		{
			maxBytes = checked(maxFileSizeMb * 1024L * 1024L);
		}
		catch (OverflowException)
		{
			maxBytes = 1024L * 1024L * 1024L;
		}

		var info = new FileInfo(_structuredLogPath);
		if (info.Length <= maxBytes)
		{
			return;
		}

		var moved = false;
		var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
		for (var suffix = 0; suffix <= 99; suffix++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var archiveName = suffix == 0
				? $"app-events-{timestamp}.jsonl"
				: $"app-events-{timestamp}-{suffix:D2}.jsonl";
			var archivePath = Path.Combine(_logsDirectoryPath, archiveName);
			if (File.Exists(archivePath))
			{
				continue;
			}

			try
			{
				File.Move(_structuredLogPath, archivePath);
				moved = true;
			}
			catch (FileNotFoundException)
			{
				return;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to rotate structured log file. source={Source}, target={Target}", _structuredLogPath, archivePath);
			}

			if (moved)
			{
				break;
			}
		}

		if (!moved)
		{
			throw new InvalidOperationException("ログローテーションに失敗しました。");
		}
	}

	private static LogViewerEntry ConvertRecord(LogRecord record)
	{
		var (message, detail, repositoryId, command, exitCode, standardOutput, standardError) = ExtractEntryData(record);
		return new LogViewerEntry(
			Timestamp: record.Timestamp,
			Severity: record.Severity,
			Name: record.Name,
			Message: message,
			Detail: detail,
			TraceId: record.Trace.TraceId,
			RepositoryId: repositoryId,
			Command: command,
			ExitCode: exitCode,
			StandardOutput: standardOutput,
			StandardError: standardError);
	}

	private static (
		string Message,
		string? Detail,
		string? RepositoryId,
		string? Command,
		int? ExitCode,
		string? StandardOutput,
		string? StandardError) ExtractEntryData(LogRecord record)
	{
		if (record.Data is JsonElement dataElement)
		{
			var message = TryGetString(dataElement, "message");
			var type = TryGetString(dataElement, "type");
			var stackTrace = TryGetString(dataElement, "stackTrace");
			var rawData = dataElement.GetRawText();
			var repositoryId = TryGetStringFromData(dataElement, "repositoryId", "RepositoryId", "repo", "Repo", "repositoryPath", "RepositoryPath");
			var command = TryGetStringFromData(dataElement, "command", "Command", "args", "Args", "arguments", "Arguments");
			var exitCode = TryGetIntFromData(dataElement, "exitCode", "ExitCode");
			var standardOutput = TryGetStringFromData(dataElement, "stdout", "StdOut", "Stdout", "standardOutput", "StandardOutput");
			var standardError = TryGetStringFromData(dataElement, "stderr", "StdErr", "Stderr", "standardError", "StandardError");

			var normalizedMessage = string.IsNullOrWhiteSpace(message)
				? record.Name
				: string.IsNullOrWhiteSpace(type) ? message! : $"{type}: {message}";
			var detail = !string.IsNullOrWhiteSpace(stackTrace)
				? stackTrace
				: IsTrivialJson(rawData) ? null : FormatJsonForDisplay(rawData);

			repositoryId ??= ExtractTokenValue(normalizedMessage, "repo=", ",", " /");
			repositoryId ??= ExtractTokenValue(detail, "repo=", ",", " /");
			command ??= ExtractTokenValue(normalizedMessage, "command=", ", reason=", ",", " /");
			command ??= ExtractTokenValue(detail, "command=", ", reason=", ",", " /");
			exitCode ??= ExtractTokenInt(normalizedMessage, "exitCode=", ",", " /");
			exitCode ??= ExtractTokenInt(detail, "exitCode=", ",", " /");

			return (normalizedMessage, detail, repositoryId, command, exitCode, standardOutput, standardError);
		}

		if (record.Data is null)
		{
			return (record.Name, null, null, null, null, null, null);
		}

		var dataText = record.Data.ToString();
		if (string.IsNullOrWhiteSpace(dataText))
		{
			return (record.Name, null, null, null, null, null, null);
		}

		var repositoryIdFromText = ExtractTokenValue(dataText, "repo=", ",", " /");
		var commandFromText = ExtractTokenValue(dataText, "command=", ", reason=", ",", " /");
		var exitCodeFromText = ExtractTokenInt(dataText, "exitCode=", ",", " /");
		return (record.Name, FormatJsonForDisplay(dataText), repositoryIdFromText, commandFromText, exitCodeFromText, null, null);
	}

	private static string? TryGetString(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var property))
		{
			return null;
		}

		return property.ValueKind switch
		{
			JsonValueKind.String => property.GetString(),
			JsonValueKind.Null => null,
			_ => property.GetRawText()
		};
	}

	private static bool IsTrivialJson(string value)
	{
		return string.Equals(value, "null", StringComparison.OrdinalIgnoreCase)
			|| value == "{}"
			|| value == "[]";
	}

	private static string FormatJsonForDisplay(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return value;
		}

		try
		{
			using var doc = JsonDocument.Parse(value);
			return JsonSerializer.Serialize(doc.RootElement, DisplayJsonOptions);
		}
		catch (JsonException)
		{
			return value;
		}
	}

	private static string? NormalizeKeyword(string? keyword)
	{
		return string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
	}

	private static bool MatchesKeyword(LogViewerEntry entry, string? keyword)
	{
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return true;
		}

		return Contains(entry.Message, keyword)
			|| Contains(entry.Detail, keyword)
			|| Contains(entry.Name, keyword)
			|| Contains(entry.TraceId, keyword)
			|| Contains(entry.RepositoryId, keyword)
			|| Contains(entry.Command, keyword)
			|| Contains(entry.ExitCode?.ToString(CultureInfo.InvariantCulture), keyword)
			|| Contains(entry.StandardOutput, keyword)
			|| Contains(entry.StandardError, keyword)
			|| Contains(entry.Severity.ToString(), keyword);
	}

	private static string? TryGetStringFromData(JsonElement element, params string[] propertyNames)
	{
		var directValue = TryGetStringFromElement(element, propertyNames);
		if (!string.IsNullOrWhiteSpace(directValue))
		{
			return directValue;
		}

		var keyValuesElement = TryGetObjectProperty(element, "keyValues")
			?? TryGetObjectProperty(element, "KeyValues");
		if (keyValuesElement is null)
		{
			return null;
		}

		return TryGetStringFromElement(keyValuesElement.Value, propertyNames);
	}

	private static int? TryGetIntFromData(JsonElement element, params string[] propertyNames)
	{
		var directValue = TryGetIntFromElement(element, propertyNames);
		if (directValue.HasValue)
		{
			return directValue.Value;
		}

		var keyValuesElement = TryGetObjectProperty(element, "keyValues")
			?? TryGetObjectProperty(element, "KeyValues");
		if (keyValuesElement is null)
		{
			return null;
		}

		return TryGetIntFromElement(keyValuesElement.Value, propertyNames);
	}

	private static string? TryGetStringFromElement(JsonElement element, params string[] propertyNames)
	{
		foreach (var propertyName in propertyNames)
		{
			var value = TryGetString(element, propertyName);
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value;
			}
		}

		return null;
	}

	private static int? TryGetIntFromElement(JsonElement element, params string[] propertyNames)
	{
		foreach (var propertyName in propertyNames)
		{
			if (!element.TryGetProperty(propertyName, out var property))
			{
				continue;
			}

			switch (property.ValueKind)
			{
				case JsonValueKind.Number when property.TryGetInt32(out var value):
					return value;
				case JsonValueKind.Number when property.TryGetInt64(out var value64)
					&& value64 <= int.MaxValue
					&& value64 >= int.MinValue:
					return (int)value64;
				case JsonValueKind.String when int.TryParse(
					property.GetString(),
					NumberStyles.Integer,
					CultureInfo.InvariantCulture,
					out var parsed):
					return parsed;
			}
		}

		return null;
	}

	private static JsonElement? TryGetObjectProperty(JsonElement element, string propertyName)
	{
		if (!element.TryGetProperty(propertyName, out var property))
		{
			return null;
		}

		return property.ValueKind == JsonValueKind.Object ? property : null;
	}

	private static string? ExtractTokenValue(string? source, string token, params string[] terminators)
	{
		if (string.IsNullOrWhiteSpace(source))
		{
			return null;
		}

		var sourceText = source!;
		var index = sourceText.IndexOf(token, StringComparison.OrdinalIgnoreCase);
		if (index < 0)
		{
			return null;
		}

		var valueStart = index + token.Length;
		if (valueStart >= sourceText.Length)
		{
			return null;
		}

		var remaining = sourceText[valueStart..];
		var valueEnd = remaining.Length;
		foreach (var terminator in terminators)
		{
			if (string.IsNullOrWhiteSpace(terminator))
			{
				continue;
			}

			var terminatorIndex = remaining.IndexOf(terminator, StringComparison.OrdinalIgnoreCase);
			if (terminatorIndex >= 0 && terminatorIndex < valueEnd)
			{
				valueEnd = terminatorIndex;
			}
		}

		var value = remaining[..valueEnd].Trim().Trim('\"');
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}

	private static int? ExtractTokenInt(string? source, string token, params string[] terminators)
	{
		var tokenValue = ExtractTokenValue(source, token, terminators);
		if (string.IsNullOrWhiteSpace(tokenValue))
		{
			return null;
		}

		return int.TryParse(tokenValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
			? value
			: null;
	}

	private static bool Contains(string? value, string keyword)
	{
		return !string.IsNullOrWhiteSpace(value)
			&& value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
	}

	private void OpenPath(string path)
	{
		try
		{
#if WINDOWS
			Process.Start(new ProcessStartInfo
			{
				FileName = "explorer.exe",
				Arguments = $"\"{path}\"",
				UseShellExecute = true
			});
#else
			Process.Start(new ProcessStartInfo
			{
				FileName = path,
				UseShellExecute = true
			});
#endif
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to open log path. path={Path}", path);
			throw new InvalidOperationException($"ログを開けませんでした。path={path}", ex);
		}
	}
}
