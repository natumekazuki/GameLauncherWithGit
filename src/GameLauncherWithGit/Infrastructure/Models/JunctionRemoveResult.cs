namespace GameLauncherWithGit.Infrastructure.Models;

public sealed record JunctionRemoveResult(
	bool IsSuccess,
	string Message,
	bool DidChangeLocalPath = false);
