namespace GameLauncherWithGit.Application.Models;

public sealed record LaunchResult(
	bool IsSuccess,
	string Message);
