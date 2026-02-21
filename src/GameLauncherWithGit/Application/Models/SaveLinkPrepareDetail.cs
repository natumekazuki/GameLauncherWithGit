namespace GameLauncherWithGit.Application.Models;

public sealed record SaveLinkPrepareDetail(
	string LinkId,
	string DisplayName,
	string LocalPath,
	string TargetPath,
	string Stage,
	bool IsSuccess,
	string Message);
