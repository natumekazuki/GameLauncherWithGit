using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Application.Models;

public sealed record LogViewerEntry(
	DateTimeOffset Timestamp,
	LogLevel Severity,
	string Name,
	string Message,
	string? Detail,
	string? TraceId);
