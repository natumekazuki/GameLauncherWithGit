using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class LocalSaveLinkOperator : ILocalSaveLinkOperator
{
	private readonly ILogger<LocalSaveLinkOperator> _logger;

	public LocalSaveLinkOperator(ILogger<LocalSaveLinkOperator> logger)
	{
		_logger = logger;
	}

	public async Task<JunctionEnsureResult> EnsureJunctionAsync(
		string localPath,
		string targetPath,
		CancellationToken cancellationToken = default)
	{
		var normalizedLocalPath = NormalizeAbsolutePath(localPath);
		if (string.IsNullOrWhiteSpace(normalizedLocalPath))
		{
			return new JunctionEnsureResult(false, $"ローカルパスが不正です。path={localPath}");
		}

		var normalizedTargetPath = NormalizeAbsolutePath(targetPath);
		if (string.IsNullOrWhiteSpace(normalizedTargetPath))
		{
			return new JunctionEnsureResult(false, $"ターゲットパスが不正です。path={targetPath}");
		}

		if (string.Equals(normalizedLocalPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
		{
			return new JunctionEnsureResult(false, "ローカルパスとターゲットパスが同一です。");
		}

#if !WINDOWS
		return new JunctionEnsureResult(false, "ジャンクション操作は Windows でのみ利用できます。");
#else
		try
		{
			Directory.CreateDirectory(normalizedTargetPath);

			if (File.Exists(normalizedLocalPath) && !Directory.Exists(normalizedLocalPath))
			{
				return new JunctionEnsureResult(
					false,
					$"ローカルパスがディレクトリではありません。path={normalizedLocalPath}");
			}

			if (Directory.Exists(normalizedLocalPath))
			{
				var resolvedLinkTarget = ResolveDirectoryLinkTarget(normalizedLocalPath);
				if (!string.IsNullOrWhiteSpace(resolvedLinkTarget))
				{
					if (AreEquivalentDirectoryPaths(resolvedLinkTarget, normalizedTargetPath))
					{
						return new JunctionEnsureResult(true, "既存ジャンクションを利用します。");
					}

					return new JunctionEnsureResult(
						false,
						$"ローカルパスは別のリンク先を指しています。path={normalizedLocalPath}, actual={resolvedLinkTarget}, expected={normalizedTargetPath}");
				}

				var localInfo = new DirectoryInfo(normalizedLocalPath);
				if (localInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
				{
					return new JunctionEnsureResult(
						false,
						$"ローカルパスのリンク先を解決できません。手動確認してください。path={normalizedLocalPath}");
				}

				return await ConvertDirectoryToJunctionAsync(
					normalizedLocalPath,
					normalizedTargetPath,
					cancellationToken);
			}

			var localParentPath = Path.GetDirectoryName(normalizedLocalPath);
			if (!string.IsNullOrWhiteSpace(localParentPath))
			{
				Directory.CreateDirectory(localParentPath);
			}

			return await CreateJunctionAsync(normalizedLocalPath, normalizedTargetPath, cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"Junction ensure failed unexpectedly. localPath={LocalPath}, targetPath={TargetPath}",
				normalizedLocalPath,
				normalizedTargetPath);
			return new JunctionEnsureResult(false, $"ジャンクション作成に失敗しました。reason={ex.Message}");
		}
#endif
	}

	public async Task<JunctionRemoveResult> RemoveJunctionWithRestoreAsync(
		string localPath,
		string targetPath,
		CancellationToken cancellationToken = default)
	{
		var normalizedLocalPath = NormalizeAbsolutePath(localPath);
		if (string.IsNullOrWhiteSpace(normalizedLocalPath))
		{
			return new JunctionRemoveResult(false, $"ローカルパスが不正です。path={localPath}");
		}

		var normalizedTargetPath = NormalizeAbsolutePath(targetPath);
		if (string.IsNullOrWhiteSpace(normalizedTargetPath))
		{
			return new JunctionRemoveResult(false, $"ターゲットパスが不正です。path={targetPath}");
		}

		if (string.Equals(normalizedLocalPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
		{
			return new JunctionRemoveResult(false, "ローカルパスとターゲットパスが同一です。");
		}

#if !WINDOWS
		return new JunctionRemoveResult(false, "ジャンクション操作は Windows でのみ利用できます。");
#else
		try
		{
			if (!TryGetPathAttributes(normalizedLocalPath, out var localAttributes))
			{
				return new JunctionRemoveResult(true, "ローカルパスが存在しないため解除不要です。");
			}

			if (!localAttributes.HasFlag(FileAttributes.Directory))
			{
				return new JunctionRemoveResult(
					false,
					$"ローカルパスがディレクトリではありません。path={normalizedLocalPath}");
			}

			var resolvedLinkTarget = ResolveDirectoryLinkTarget(normalizedLocalPath);
			if (string.IsNullOrWhiteSpace(resolvedLinkTarget))
			{
				if (localAttributes.HasFlag(FileAttributes.ReparsePoint))
				{
					if (!Directory.Exists(normalizedTargetPath))
					{
						return await RemoveBrokenJunctionAsync(normalizedLocalPath, normalizedTargetPath);
					}

					return new JunctionRemoveResult(
						false,
						$"ローカルパスのリンク先を解決できません。手動確認してください。path={normalizedLocalPath}");
				}

				return new JunctionRemoveResult(true, "ローカルパスは通常フォルダのため解除不要です。");
			}

			if (!AreEquivalentDirectoryPaths(resolvedLinkTarget, normalizedTargetPath))
			{
				return new JunctionRemoveResult(
					false,
					$"ローカルパスは別のリンク先を指しています。path={normalizedLocalPath}, actual={resolvedLinkTarget}, expected={normalizedTargetPath}");
			}

			if (!Directory.Exists(normalizedTargetPath))
			{
				return new JunctionRemoveResult(
					false,
					$"ターゲットディレクトリが存在しません。path={normalizedTargetPath}");
			}

			return await RemoveJunctionAndRestoreDirectoryAsync(
				normalizedLocalPath,
				normalizedTargetPath,
				cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"Junction removal failed unexpectedly. localPath={LocalPath}, targetPath={TargetPath}",
				normalizedLocalPath,
				normalizedTargetPath);
			return new JunctionRemoveResult(false, $"ジャンクション解除に失敗しました。reason={ex.Message}");
		}
#endif
	}

	public async Task<DirectoryHydrationResult> HydrateDirectoryAsync(
		string targetPath,
		CancellationToken cancellationToken = default)
	{
		var normalizedTargetPath = NormalizeAbsolutePath(targetPath);
		if (string.IsNullOrWhiteSpace(normalizedTargetPath))
		{
			return new DirectoryHydrationResult(false, $"ターゲットパスが不正です。path={targetPath}", 0, 0);
		}

		if (!Directory.Exists(normalizedTargetPath))
		{
			return new DirectoryHydrationResult(false, $"ターゲットディレクトリが存在しません。path={normalizedTargetPath}", 0, 0);
		}

		var fileCount = 0;
		long totalBytes = 0;
		var buffer = new byte[64 * 1024];

		try
		{
			foreach (var filePath in Directory.EnumerateFiles(normalizedTargetPath, "*", SearchOption.AllDirectories))
			{
				cancellationToken.ThrowIfCancellationRequested();

				await using var stream = new FileStream(
					filePath,
					FileMode.Open,
					FileAccess.Read,
					FileShare.ReadWrite | FileShare.Delete,
					bufferSize: buffer.Length,
					options: FileOptions.SequentialScan);
				while (true)
				{
					var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
					if (bytesRead <= 0)
					{
						break;
					}

					totalBytes += bytesRead;
				}

				fileCount++;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Directory hydration failed. targetPath={TargetPath}", normalizedTargetPath);
			return new DirectoryHydrationResult(
				false,
				$"リンク先ファイルの読了に失敗しました。path={normalizedTargetPath}, reason={ex.Message}",
				fileCount,
				totalBytes);
		}

		return new DirectoryHydrationResult(
			true,
			$"リンク先ファイルを読了しました。files={fileCount}, bytes={totalBytes}",
			fileCount,
			totalBytes);
	}

#if WINDOWS
	private async Task<JunctionRemoveResult> RemoveJunctionAndRestoreDirectoryAsync(
		string localPath,
		string targetPath,
		CancellationToken cancellationToken)
	{
		var restorePath = BuildRestorePath(localPath);
		var copyResult = CopyDirectoryStrict(targetPath, restorePath, cancellationToken);
		if (!copyResult.IsSuccess)
		{
			if (copyResult.IsCanceled)
			{
				TryDeleteDirectory(restorePath);
				throw new OperationCanceledException(copyResult.Message, cancellationToken);
			}

			TryDeleteDirectory(restorePath);
			return new JunctionRemoveResult(
				false,
				$"リンク解除前の復元コピーに失敗しました。local={localPath}, target={targetPath}, reason={copyResult.Message}");
		}

		var isJunctionDeleted = false;
		try
		{
			Directory.Delete(localPath);
			isJunctionDeleted = true;
			Directory.Move(restorePath, localPath);
			return new JunctionRemoveResult(true, "ジャンクションを解除し、ローカル通常フォルダへ復元しました。");
		}
		catch (OperationCanceledException)
		{
			if (isJunctionDeleted)
			{
				await TryRecreateJunctionAsync(localPath, targetPath);
			}

			throw;
		}
		catch (Exception ex)
		{
			if (isJunctionDeleted)
			{
				await TryRecreateJunctionAsync(localPath, targetPath);
			}
			else
			{
				TryDeleteDirectory(restorePath);
			}

			var restoreHint = isJunctionDeleted
				? $", restore={restorePath}"
				: string.Empty;
			return new JunctionRemoveResult(
				false,
				$"ジャンクション解除に失敗しました。local={localPath}, target={targetPath}, reason={ex.Message}{restoreHint}");
		}
	}

	private async Task<JunctionRemoveResult> RemoveBrokenJunctionAsync(string localPath, string targetPath)
	{
		try
		{
			Directory.Delete(localPath);
			Directory.CreateDirectory(localPath);
			return new JunctionRemoveResult(
				true,
				"リンク先が消失したジャンクションを解除し、空のローカルフォルダを復元しました。");
		}
		catch (Exception ex)
		{
			await TryRecreateJunctionAsync(localPath, targetPath);
			return new JunctionRemoveResult(
				false,
				$"リンク先消失ジャンクションの解除に失敗しました。local={localPath}, target={targetPath}, reason={ex.Message}");
		}
	}

	private async Task TryRecreateJunctionAsync(string localPath, string targetPath)
	{
		try
		{
			if (Directory.Exists(localPath))
			{
				return;
			}

			var localParentPath = Path.GetDirectoryName(localPath);
			if (!string.IsNullOrWhiteSpace(localParentPath))
			{
				Directory.CreateDirectory(localParentPath);
			}

			var recreateResult = await CreateJunctionAsync(localPath, targetPath, CancellationToken.None);
			if (!recreateResult.IsSuccess)
			{
				_logger.LogWarning(
					"Junction recovery failed. localPath={LocalPath}, targetPath={TargetPath}, reason={Reason}",
					localPath,
					targetPath,
					recreateResult.Message);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(
				ex,
				"Junction recovery failed unexpectedly. localPath={LocalPath}, targetPath={TargetPath}",
				localPath,
				targetPath);
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
			// クリーンアップ失敗は処理結果に影響させない。
		}
	}

	private async Task<JunctionEnsureResult> ConvertDirectoryToJunctionAsync(
		string localPath,
		string targetPath,
		CancellationToken cancellationToken)
	{
		var backupPath = BuildBackupPath(localPath);
		Directory.Move(localPath, backupPath);
		CopyDirectoryResult? copyResult = null;

		try
		{
			copyResult = CopyDirectoryStrict(backupPath, targetPath, cancellationToken);
			if (!copyResult.IsSuccess)
			{
				if (copyResult.IsCanceled)
				{
					throw new OperationCanceledException(copyResult.Message, cancellationToken);
				}

				throw new InvalidOperationException(copyResult.Message);
			}

			var createResult = await CreateJunctionAsync(localPath, targetPath, cancellationToken);
			if (!createResult.IsSuccess)
			{
				throw new InvalidOperationException(createResult.Message);
			}

			TryDeleteBackupDirectory(backupPath, localPath, targetPath);
			return new JunctionEnsureResult(true, "既存フォルダを移行してジャンクションを作成しました。");
		}
		catch (OperationCanceledException)
		{
			RollbackConversion(localPath, backupPath, copyResult);
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(
				ex,
				"Directory to junction conversion failed. localPath={LocalPath}, targetPath={TargetPath}",
				localPath,
				targetPath);
			RollbackConversion(localPath, backupPath, copyResult);
			return new JunctionEnsureResult(false, $"既存フォルダの移行に失敗しました。path={localPath}, reason={ex.Message}");
		}
	}

	private static void RollbackConversion(string localPath, string backupPath, CopyDirectoryResult? copyResult)
	{
		RollbackTargetDirectory(copyResult);
		RollbackLocalDirectory(localPath, backupPath);
	}

	private void TryDeleteBackupDirectory(string backupPath, string localPath, string targetPath)
	{
		try
		{
			if (Directory.Exists(backupPath))
			{
				Directory.Delete(backupPath, recursive: true);
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(
				ex,
				"Backup cleanup failed after successful junction conversion. backupPath={BackupPath}, localPath={LocalPath}, targetPath={TargetPath}",
				backupPath,
				localPath,
				targetPath);
		}
	}

	private static void RollbackTargetDirectory(CopyDirectoryResult? copyResult)
	{
		if (copyResult is null)
		{
			return;
		}

		foreach (var filePath in copyResult.CreatedFiles)
		{
			try
			{
				if (File.Exists(filePath))
				{
					File.Delete(filePath);
				}
			}
			catch
			{
				// ターゲット側ロールバック失敗は、ローカル復旧継続を優先する。
			}
		}

		foreach (var directoryPath in copyResult.CreatedDirectories.OrderByDescending(static path => path.Length))
		{
			try
			{
				if (!Directory.Exists(directoryPath))
				{
					continue;
				}

				if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
				{
					continue;
				}

				Directory.Delete(directoryPath);
			}
			catch
			{
				// ディレクトリ削除失敗は無視し、復旧処理の継続を優先する。
			}
		}
	}

	private static void RollbackLocalDirectory(string localPath, string backupPath)
	{
		try
		{
			if (Directory.Exists(localPath))
			{
				var localInfo = new DirectoryInfo(localPath);
				if (localInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
				{
					Directory.Delete(localPath);
				}
			}
		}
		catch
		{
			// ロールバック中の削除失敗は復旧処理継続を優先する。
		}

		try
		{
			if (!Directory.Exists(localPath) && Directory.Exists(backupPath))
			{
				Directory.Move(backupPath, localPath);
			}
		}
		catch
		{
			// 最終的な復旧失敗は呼び出し側にエラーメッセージで通知する。
		}
	}

	private static CopyDirectoryResult CopyDirectoryStrict(
		string sourcePath,
		string destinationPath,
		CancellationToken cancellationToken)
	{
		var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var createdFiles = new List<string>();
		CopyDirectoryResult BuildCopyResult(bool isSuccess, string message, bool isCanceled = false)
		{
			return new CopyDirectoryResult(
				isSuccess,
				message,
				isCanceled,
				createdDirectories
					.OrderByDescending(static path => path.Length)
					.ToArray(),
				createdFiles.AsReadOnly());
		}

		try
		{
			Directory.CreateDirectory(destinationPath);

			var sourceDirectories = Directory
				.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories)
				.OrderBy(static path => path.Length)
				.ToArray();
			var sourceFiles = Directory
				.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
				.ToArray();

			foreach (var sourceDirectory in sourceDirectories)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var relativePath = Path.GetRelativePath(sourcePath, sourceDirectory);
				var destinationDirectoryPath = Path.Combine(destinationPath, relativePath);
				if (File.Exists(destinationDirectoryPath))
				{
					return BuildCopyResult(false, $"既存ファイルと競合しました。path={destinationDirectoryPath}");
				}
			}

			foreach (var sourceFile in sourceFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var relativePath = Path.GetRelativePath(sourcePath, sourceFile);
				var destinationFilePath = Path.Combine(destinationPath, relativePath);
				if (File.Exists(destinationFilePath) || Directory.Exists(destinationFilePath))
				{
					return BuildCopyResult(false, $"既存ファイルと競合しました。path={destinationFilePath}");
				}
			}

			foreach (var sourceDirectory in sourceDirectories)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var relativePath = Path.GetRelativePath(sourcePath, sourceDirectory);
				var destinationDirectoryPath = Path.Combine(destinationPath, relativePath);
				if (!Directory.Exists(destinationDirectoryPath))
				{
					Directory.CreateDirectory(destinationDirectoryPath);
					createdDirectories.Add(destinationDirectoryPath);
				}
			}

			foreach (var sourceFile in sourceFiles)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var relativePath = Path.GetRelativePath(sourcePath, sourceFile);
				var destinationFilePath = Path.Combine(destinationPath, relativePath);
				var destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
				if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
				{
					Directory.CreateDirectory(destinationDirectoryPath);
				}

				File.Copy(sourceFile, destinationFilePath, overwrite: false);
				createdFiles.Add(destinationFilePath);
			}

			return BuildCopyResult(true, $"既存データをリンク先へ移行しました。files={sourceFiles.Length}");
		}
		catch (OperationCanceledException)
		{
			return BuildCopyResult(false, "既存データの移行がキャンセルされました。", isCanceled: true);
		}
		catch (Exception ex)
		{
			return BuildCopyResult(false, $"既存データの移行中に失敗しました。reason={ex.Message}");
		}
	}

	private async Task<JunctionEnsureResult> CreateJunctionAsync(
		string localPath,
		string targetPath,
		CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "cmd.exe",
			Arguments = $"/c mklink /J \"{localPath}\" \"{targetPath}\"",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using var process = new Process
		{
			StartInfo = startInfo
		};

		try
		{
			process.Start();
			var readStdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var readStdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken);

			var standardOutput = await readStdOutTask;
			var standardError = await readStdErrTask;
			if (process.ExitCode == 0)
			{
				return new JunctionEnsureResult(true, "ジャンクションを作成しました。");
			}

			var reason = FirstNonEmptyLine(standardError)
				?? FirstNonEmptyLine(standardOutput)
				?? $"exit code: {process.ExitCode}";
			return new JunctionEnsureResult(
				false,
				$"ジャンクション作成に失敗しました。local={localPath}, target={targetPath}, reason={reason}");
		}
		catch (OperationCanceledException)
		{
			TryKillProcess(process);
			throw;
		}
		catch (Exception ex)
		{
			return new JunctionEnsureResult(
				false,
				$"ジャンクション作成に失敗しました。local={localPath}, target={targetPath}, reason={ex.Message}");
		}
	}

	private static void TryKillProcess(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
		}
	}

	private static string BuildBackupPath(string localPath)
	{
		var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
		var normalizedLocalPath = NormalizeDirectoryPathForComparison(localPath);
		return $"{normalizedLocalPath}.backup-{timestamp}-{Guid.NewGuid():N}";
	}

	private static string BuildRestorePath(string localPath)
	{
		var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
		var normalizedLocalPath = NormalizeDirectoryPathForComparison(localPath);
		return $"{normalizedLocalPath}.restore-{timestamp}-{Guid.NewGuid():N}";
	}

	private static string? ResolveDirectoryLinkTarget(string localPath)
	{
		try
		{
			var info = new DirectoryInfo(localPath);
			if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				return null;
			}

			var linkTarget = info.ResolveLinkTarget(returnFinalTarget: false);
			if (linkTarget is null)
			{
				return null;
			}

			return NormalizeAbsolutePath(linkTarget.FullName);
		}
		catch
		{
			return null;
		}
	}

	private sealed record CopyDirectoryResult(
		bool IsSuccess,
		string Message,
		bool IsCanceled,
		IReadOnlyList<string> CreatedDirectories,
		IReadOnlyList<string> CreatedFiles);
#endif

	private static string? NormalizeAbsolutePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		try
		{
			var normalized = Path.GetFullPath(path.Trim());
			return Path.IsPathRooted(normalized) ? normalized : null;
		}
		catch
		{
			return null;
		}
	}

	private static bool AreEquivalentDirectoryPaths(string pathA, string pathB)
	{
		var normalizedPathA = NormalizeDirectoryPathForComparison(pathA);
		var normalizedPathB = NormalizeDirectoryPathForComparison(pathB);
		return string.Equals(normalizedPathA, normalizedPathB, StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizeDirectoryPathForComparison(string path)
	{
		var normalizedPath = NormalizeAbsolutePath(path) ?? path.Trim();
		return TrimTrailingDirectorySeparators(normalizedPath);
	}

	private static string TrimTrailingDirectorySeparators(string path)
	{
		var trimmed = path.Trim();
		if (string.IsNullOrWhiteSpace(trimmed))
		{
			return string.Empty;
		}

		var root = Path.GetPathRoot(trimmed);
		if (!string.IsNullOrWhiteSpace(root)
			&& string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
		{
			return trimmed;
		}

		return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}

	private static bool TryGetPathAttributes(string path, out FileAttributes attributes)
	{
		try
		{
			attributes = File.GetAttributes(path);
			return true;
		}
		catch
		{
			attributes = default;
			return false;
		}
	}

	private static string? FirstNonEmptyLine(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		return value
			.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line));
	}
}
