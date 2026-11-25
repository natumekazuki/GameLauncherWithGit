namespace GameLauncherWithGit.Application.Models;

public sealed record LaunchResult(
	bool IsSuccess,
	string Message,
	bool ShowCapture = false)
{
	/// <summary>Whether a follow-up step failed after the game successfully started.</summary>
	internal bool HasWarnings { get; init; }
}
