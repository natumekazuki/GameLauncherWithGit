namespace GameLauncherWithGit.Application.Models;

public sealed record GameSaveLinkEditInput(
	string? Id,
	string DisplayName,
	string LocalPath,
	string TargetPath,
	bool EnsureOnLaunch);
