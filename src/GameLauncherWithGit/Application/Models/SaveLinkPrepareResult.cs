namespace GameLauncherWithGit.Application.Models;

public sealed record SaveLinkPrepareResult(
	bool IsSuccess,
	IReadOnlyList<SaveLinkPrepareDetail> Details)
{
	public static SaveLinkPrepareResult Success { get; } = new(true, Array.Empty<SaveLinkPrepareDetail>());
}
