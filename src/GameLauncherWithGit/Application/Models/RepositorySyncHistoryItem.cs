namespace GameLauncherWithGit.Application.Models;

public sealed record RepositorySyncHistoryItem(
	long Id,
	string RepositoryId,
	RepositorySyncHistoryStatus Status,
	DateTimeOffset StartedAt,
	DateTimeOffset FinishedAt,
	long DurationMs,
	string? Command,
	string? Reason);
