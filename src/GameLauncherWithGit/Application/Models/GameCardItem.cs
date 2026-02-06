namespace GameLauncherWithGit.Application.Models;

public sealed record GameCardItem(
	string Id,
	string Title,
	string ExecutablePath,
	string? RelatedRepositoryPath,
	DateTimeOffset? LastPlayedAt,
	GameCardStatus Status);
