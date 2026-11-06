namespace GameLauncherWithGit.Application.Services;

internal static class AppDataPaths
{
	private static readonly string? ConfiguredDirectory = Environment.GetEnvironmentVariable("GAMELAUNCHER_DATA_DIRECTORY");

	public static bool IsIsolated => ConfiguredDirectory is not null;

	public static string Directory { get; } = ResolveDirectory();

	private static string ResolveDirectory()
	{
		if (ConfiguredDirectory is null)
		{
			return FileSystem.AppDataDirectory;
		}

		if (!Path.IsPathFullyQualified(ConfiguredDirectory))
		{
			throw new InvalidOperationException("GAMELAUNCHER_DATA_DIRECTORYには絶対パスを指定してください。");
		}

		return Path.GetFullPath(ConfiguredDirectory);
	}
}
