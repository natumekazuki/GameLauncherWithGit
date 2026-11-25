using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;

namespace GameLauncherWithGit.Infrastructure.Services;

internal static class TraySyncTargets
{
	internal static string[] GetDistinctRepositories(IEnumerable<GameCardItem> games) => games
		.Select(static game => NormalizeRepositoryPath(game.RelatedRepositoryPath))
		.OfType<string>()
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.ToArray();

	private static string? NormalizeRepositoryPath(string? path)
	{
		if (string.IsNullOrWhiteSpace(path)) return null;
		try
		{
			var normalized = Path.GetFullPath(path.Trim());
			return Directory.Exists(normalized)
				? SyncOrchestrator.NormalizeRepositoryKey(normalized, GitService.ResolveDirectoryPath)
				: null;
		}
		catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return null;
		}
	}
}
