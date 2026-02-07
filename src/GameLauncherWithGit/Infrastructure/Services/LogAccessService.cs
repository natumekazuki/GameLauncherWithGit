using System.Diagnostics;
using System.Text;
using GameLauncherWithGit;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using MonochromeMemory.Log.Core;

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
	private readonly IReadOnlyDictionary<string, object?> _resource;

	public LogAccessService(
		ILogDispatcher logDispatcher,
		ILogger<LogAccessService> logger)
	{
		_logDispatcher = logDispatcher;
		_logger = logger;
		_logsDirectoryPath = Path.Combine(AppDataPaths.BaseDirectory, LogsDirectoryName);
		_structuredLogPath = Path.Combine(_logsDirectoryPath, StructuredLogFileName);
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
