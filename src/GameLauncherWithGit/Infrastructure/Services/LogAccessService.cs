using System.Diagnostics;
using System.Text;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class LogAccessService : ILogAccessService
{
	private const string LogsDirectoryName = "logs";
	private const string ErrorLogFileName = "app-errors.log";

	private readonly ILogger<LogAccessService> _logger;
	private readonly string _logsDirectoryPath;
	private readonly string _errorLogPath;

	public LogAccessService(ILogger<LogAccessService> logger)
	{
		_logger = logger;
		_logsDirectoryPath = Path.Combine(FileSystem.AppDataDirectory, LogsDirectoryName);
		_errorLogPath = Path.Combine(_logsDirectoryPath, ErrorLogFileName);
	}

	public async Task AppendErrorAsync(string message, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		EnsureLogsDirectory();
		var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz");
		var line = $"{timestamp} {message}{Environment.NewLine}";
		await File.AppendAllTextAsync(_errorLogPath, line, Encoding.UTF8, cancellationToken);
	}

	public Task OpenLatestErrorLogAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureLogsDirectory();

		if (!File.Exists(_errorLogPath))
		{
			var header = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ログファイルを作成しました。{Environment.NewLine}";
			File.WriteAllText(_errorLogPath, header, Encoding.UTF8);
		}

		OpenPath(_errorLogPath);
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
