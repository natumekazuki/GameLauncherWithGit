namespace GameLauncherWithGit.Infrastructure.Models;

public sealed record JunctionEnsureResult(
	bool IsSuccess,
	string Message);
