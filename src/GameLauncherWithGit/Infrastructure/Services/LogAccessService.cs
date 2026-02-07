using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
	private const string ServiceName = "GameLauncherWithGit";

	private readonly ILogDispatcher _logDispatcher;
	private readonly ILogger<LogAccessService> _logger;
	private readonly string _logsDirectoryPath;
	private readonly string _structuredLogPath;
	private readonly ILogStoreReader _logStoreReader;
	private readonly IReadOnlyDictionary<string, object?> _resource;

	public LogAccessService(
		ILogDispatcher logDispatcher,
		ILogger<LogAccessService> logger)
	{
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

	private static LogViewerEntry ConvertRecord(LogRecord record)
	{
		var (message, detail) = ExtractMessageAndDetail(record);
		return new LogViewerEntry(
			Timestamp: record.Timestamp,
			Severity: record.Severity,
			Name: record.Name,
			Message: message,
			Detail: detail,
			TraceId: record.Trace.TraceId);
	}

	private static (string Message, string? Detail) ExtractMessageAndDetail(LogRecord record)
	{
		if (record.Data is JsonElement dataElement)
		{
			var message = TryGetString(dataElement, "message");
			var type = TryGetString(dataElement, "type");
			var stackTrace = TryGetString(dataElement, "stackTrace");
			var rawData = dataElement.GetRawText();

			var normalizedMessage = string.IsNullOrWhiteSpace(message)
				? record.Name
				: string.IsNullOrWhiteSpace(type) ? message! : $"{type}: {message}";
			var detail = !string.IsNullOrWhiteSpace(stackTrace)
				? stackTrace
				: IsTrivialJson(rawData) ? null : rawData;
			return (normalizedMessage, detail);
		}

		if (record.Data is null)
		{
			return (record.Name, null);
		}

		var dataText = record.Data.ToString();
		return string.IsNullOrWhiteSpace(dataText)
			? (record.Name, null)
			: (record.Name, dataText);
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
			|| Contains(entry.Severity.ToString(), keyword);
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
