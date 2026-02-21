namespace GameLauncherWithGit.Infrastructure.Models;

public sealed record DirectoryHydrationResult(
	bool IsSuccess,
	string Message,
	int FileCount,
	long TotalBytes);
