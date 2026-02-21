namespace GameLauncherWithGit.Application.Models;

public sealed record GameSaveLinkItem(
	string Id,
	string GameId,
	string DisplayName,
	string LocalPath,
	string TargetPath,
	int OrderNo,
	bool EnsureOnLaunch);
