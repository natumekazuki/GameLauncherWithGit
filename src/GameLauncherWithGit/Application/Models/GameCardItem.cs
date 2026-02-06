namespace GameLauncherWithGit.Application.Models;

public sealed record GameCardItem(
	string Id,
	string Title,
	string ExecutablePath,
	DateTimeOffset? LastPlayedAt,
	GameCardStatus Status);
