namespace GameLauncherWithGit.Application.Models;

public sealed record GameEditInput(
	string Title,
	string ExecutablePath,
	string? RelatedRepositoryPath);
