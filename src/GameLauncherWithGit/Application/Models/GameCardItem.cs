namespace GameLauncherWithGit.Application.Models;

public sealed record GameCardItem(
	string Id,
	string Title,
	string ExecutablePath,
	IReadOnlyList<string> RelatedRepositoryPaths,
	DateTimeOffset? LastPlayedAt,
	GameCardStatus Status);
