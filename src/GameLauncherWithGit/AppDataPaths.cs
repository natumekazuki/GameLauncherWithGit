using Microsoft.Maui.Storage;

namespace GameLauncherWithGit;

public static class AppDataPaths
{
	private const string AppDirectoryName = "GameLauncherWithGit";
	private const string LegacyPublisherDirectoryName = "User Name";
	private static readonly string[] LegacyApplicationIds =
	[
		"com.companyname.gamelauncherwithgit",
		"com.monochromememory.gamelauncherwithgit"
	];

	private static readonly object Gate = new();
	private static readonly string BaseDirectoryPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		AppDirectoryName);
	private static bool _isInitialized;

	public static string BaseDirectory
	{
		get
		{
			EnsureInitialized();
			return BaseDirectoryPath;
		}
	}

	private static void EnsureInitialized()
	{
		lock (Gate)
		{
			if (_isInitialized)
			{
				return;
			}

			Directory.CreateDirectory(BaseDirectoryPath);
			MigrateLegacyData();
			_isInitialized = true;
		}
	}

	private static void MigrateLegacyData()
	{
		var targetFullPath = Path.GetFullPath(BaseDirectoryPath);
		foreach (var legacyPath in EnumerateLegacyCandidates())
		{
			if (string.IsNullOrWhiteSpace(legacyPath) || !Directory.Exists(legacyPath))
			{
				continue;
			}

			var legacyFullPath = Path.GetFullPath(legacyPath);
			if (string.Equals(legacyFullPath, targetFullPath, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			try
			{
				CopyDirectoryMerge(legacyFullPath, targetFullPath);
			}
			catch
			{
				// 移行失敗時も起動継続する。
			}
		}
	}

	private static IEnumerable<string> EnumerateLegacyCandidates()
	{
		yield return FileSystem.AppDataDirectory;

		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		foreach (var legacyApplicationId in LegacyApplicationIds)
		{
			yield return Path.Combine(
				localAppData,
				LegacyPublisherDirectoryName,
				legacyApplicationId,
				"Data");
		}
	}

	private static void CopyDirectoryMerge(string sourceDirectoryPath, string destinationDirectoryPath)
	{
		Directory.CreateDirectory(destinationDirectoryPath);

		foreach (var filePath in Directory.EnumerateFiles(sourceDirectoryPath))
		{
			var destinationFilePath = Path.Combine(destinationDirectoryPath, Path.GetFileName(filePath));
			if (!File.Exists(destinationFilePath))
			{
				File.Copy(filePath, destinationFilePath, overwrite: false);
			}
		}

		foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectoryPath))
		{
			var destinationChildDirectoryPath = Path.Combine(destinationDirectoryPath, Path.GetFileName(directoryPath));
			CopyDirectoryMerge(directoryPath, destinationChildDirectoryPath);
		}
	}
}
