using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Application.Services;

public sealed class SaveLinkService : ISaveLinkService
{
	private readonly ISaveLinkStore _saveLinkStore;
	private readonly ILocalSaveLinkOperator _localSaveLinkOperator;
	private readonly ILogger<SaveLinkService> _logger;

	public SaveLinkService(
		ISaveLinkStore saveLinkStore,
		ILocalSaveLinkOperator localSaveLinkOperator,
		ILogger<SaveLinkService> logger)
	{
		_saveLinkStore = saveLinkStore;
		_localSaveLinkOperator = localSaveLinkOperator;
		_logger = logger;
	}

	public Task<IReadOnlyList<GameSaveLinkItem>> GetByGameIdAsync(
		string gameId,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return Task.FromResult<IReadOnlyList<GameSaveLinkItem>>(Array.Empty<GameSaveLinkItem>());
		}

		return _saveLinkStore.GetByGameIdAsync(gameId.Trim(), cancellationToken);
	}

	public async Task ReplaceForGameAsync(
		string gameId,
		IReadOnlyList<GameSaveLinkEditInput> links,
		CancellationToken cancellationToken = default)
	{
		var normalized = NormalizeForGame(gameId, links);
		await _saveLinkStore.ReplaceByGameIdAsync(gameId.Trim(), normalized, cancellationToken);
	}

	public IReadOnlyList<GameSaveLinkItem> NormalizeForGame(
		string gameId,
		IReadOnlyList<GameSaveLinkEditInput> links)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			throw new InvalidOperationException("セーブリンク検証に失敗しました。ゲームIDが不正です。");
		}

		return NormalizeInputs(gameId.Trim(), links);
	}

	public void ValidateForGame(
		string gameId,
		IReadOnlyList<GameSaveLinkEditInput> links)
	{
		_ = NormalizeForGame(gameId, links);
	}

	public async Task UnlinkRemovedLinksAsync(
		string gameId,
		IReadOnlyList<GameSaveLinkItem> nextLinks,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			throw new InvalidOperationException("セーブリンク解除に失敗しました。ゲームIDが不正です。");
		}

		var normalizedGameId = gameId.Trim();
		var existingLinks = await _saveLinkStore.GetByGameIdAsync(normalizedGameId, cancellationToken);
		if (existingLinks.Count == 0)
		{
			return;
		}

		var nextLinkKeys = BuildLinkEndpointKeySet(nextLinks);
		var unlinkedLinks = new List<GameSaveLinkItem>();
		foreach (var existingLink in existingLinks.OrderBy(static link => link.OrderNo))
		{
			cancellationToken.ThrowIfCancellationRequested();

			var existingKey = BuildLinkEndpointKey(existingLink.LocalPath, existingLink.TargetPath);
			if (nextLinkKeys.Contains(existingKey))
			{
				continue;
			}

			var removeResult = await _localSaveLinkOperator.RemoveJunctionWithRestoreAsync(
				existingLink.LocalPath,
				existingLink.TargetPath,
				cancellationToken);
			if (removeResult.IsSuccess)
			{
				if (removeResult.DidChangeLocalPath)
				{
					unlinkedLinks.Add(existingLink);
				}

				continue;
			}

			_logger.LogWarning(
				"Save-link unlink failed. gameId={GameId}, linkId={LinkId}, localPath={LocalPath}, targetPath={TargetPath}, reason={Reason}",
				normalizedGameId,
				existingLink.Id,
				existingLink.LocalPath,
				existingLink.TargetPath,
				removeResult.Message);

			var rollbackError = await TryRollbackUnlinkedLinksAsync(normalizedGameId, unlinkedLinks);
			if (string.IsNullOrWhiteSpace(rollbackError))
			{
				throw new InvalidOperationException(
					$"セーブリンク解除に失敗したため、先行解除分をロールバックしました。local={existingLink.LocalPath}, target={existingLink.TargetPath}, reason={removeResult.Message}");
			}

			throw new InvalidOperationException(
				$"セーブリンク解除に失敗し、先行解除分ロールバックにも失敗しました。local={existingLink.LocalPath}, target={existingLink.TargetPath}, reason={removeResult.Message}, rollbackReason={rollbackError}");
		}
	}

	public async Task<SaveLinkPrepareResult> EnsureReadyForLaunchAsync(
		string gameId,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			return new SaveLinkPrepareResult(
				false,
				[
					new SaveLinkPrepareDetail(
						LinkId: string.Empty,
						DisplayName: "不明",
						LocalPath: string.Empty,
						TargetPath: string.Empty,
						Stage: "validate",
						IsSuccess: false,
						Message: "ゲームIDが不正です。")
				]);
		}

		var links = await _saveLinkStore.GetByGameIdAsync(gameId.Trim(), cancellationToken);
		var targetLinks = links
			.Where(static link => link.EnsureOnLaunch)
			.OrderBy(static link => link.OrderNo)
			.ToArray();
		if (targetLinks.Length == 0)
		{
			return SaveLinkPrepareResult.Success;
		}

		var details = new List<SaveLinkPrepareDetail>(targetLinks.Length * 2);
		foreach (var link in targetLinks)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var ensureResult = await _localSaveLinkOperator.EnsureJunctionAsync(
				link.LocalPath,
				link.TargetPath,
				cancellationToken);
			details.Add(
				new SaveLinkPrepareDetail(
					LinkId: link.Id,
					DisplayName: link.DisplayName,
					LocalPath: link.LocalPath,
					TargetPath: link.TargetPath,
					Stage: "junction",
					IsSuccess: ensureResult.IsSuccess,
					Message: ensureResult.Message));
			if (!ensureResult.IsSuccess)
			{
				_logger.LogWarning(
					"Junction ensure failed. gameId={GameId}, linkId={LinkId}, localPath={LocalPath}, targetPath={TargetPath}, reason={Reason}",
					gameId,
					link.Id,
					link.LocalPath,
					link.TargetPath,
					ensureResult.Message);
				return new SaveLinkPrepareResult(false, details);
			}

			var hydrationResult = await _localSaveLinkOperator.HydrateDirectoryAsync(
				link.TargetPath,
				cancellationToken);
			details.Add(
				new SaveLinkPrepareDetail(
					LinkId: link.Id,
					DisplayName: link.DisplayName,
					LocalPath: link.LocalPath,
					TargetPath: link.TargetPath,
					Stage: "hydrate",
					IsSuccess: hydrationResult.IsSuccess,
					Message: hydrationResult.Message));
			if (!hydrationResult.IsSuccess)
			{
				_logger.LogWarning(
					"Directory hydration failed. gameId={GameId}, linkId={LinkId}, targetPath={TargetPath}, reason={Reason}",
					gameId,
					link.Id,
					link.TargetPath,
					hydrationResult.Message);
				return new SaveLinkPrepareResult(false, details);
			}
		}

		return new SaveLinkPrepareResult(true, details);
	}

	private static IReadOnlyList<GameSaveLinkItem> NormalizeInputs(
		string gameId,
		IReadOnlyList<GameSaveLinkEditInput>? links)
	{
		if (links is null || links.Count == 0)
		{
			return Array.Empty<GameSaveLinkItem>();
		}

		var normalizedInputs = new List<NormalizedSaveLinkInput>(links.Count);
		for (var index = 0; index < links.Count; index++)
		{
			var entry = links[index];
			normalizedInputs.Add(NormalizeSingleInput(index, entry));
		}

		ValidateCrossLinkEndpointConflicts(normalizedInputs);
		return normalizedInputs
			.Select(input => ToGameSaveLinkItem(gameId, input))
			.ToArray();
	}

	private static void EnsureNoCrossLinkPathConflict(
		int linkIndex,
		string endpointLabel,
		string endpointPath,
		IReadOnlyList<SaveLinkEndpointConstraint> existingEndpoints)
	{
		foreach (var existing in existingEndpoints)
		{
			if (!IsAncestorOrDescendantPath(endpointPath, existing.Path))
			{
				continue;
			}

			var relation = string.Equals(endpointPath, existing.Path, StringComparison.OrdinalIgnoreCase)
				? "同一"
				: "親子関係";
			throw new InvalidOperationException(
				$"セーブリンク {linkIndex + 1} の{endpointLabel}が、セーブリンク {existing.LinkIndex + 1} の{existing.Label}と{relation}です。リンク間で Local/Target の重複・相互参照は設定できません。path={endpointPath}, other={existing.Path}");
		}
	}

	private static NormalizedSaveLinkInput NormalizeSingleInput(int index, GameSaveLinkEditInput entry)
	{
		var localPath = NormalizeAbsolutePath(entry.LocalPath);
		var targetPath = NormalizeAbsolutePath(entry.TargetPath);
		if (string.IsNullOrWhiteSpace(localPath))
		{
			throw new InvalidOperationException($"セーブリンク {index + 1} のローカルパスが未設定です。");
		}

		if (string.IsNullOrWhiteSpace(targetPath))
		{
			throw new InvalidOperationException($"セーブリンク {index + 1} のターゲットパスが未設定です。");
		}

		if (string.Equals(localPath, targetPath, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException(
				$"セーブリンク {index + 1} のローカルパスとターゲットパスが同一です。別のパスを指定してください。");
		}

		if (IsAncestorOrDescendantPath(localPath, targetPath))
		{
			throw new InvalidOperationException(
				$"セーブリンク {index + 1} のローカルパスとターゲットパスは親子関係にできません。別階層のパスを指定してください。");
		}

		var id = string.IsNullOrWhiteSpace(entry.Id)
			? $"save-link-{Guid.NewGuid():N}"
			: entry.Id.Trim();
		var displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
			? BuildDefaultDisplayName(localPath)
			: entry.DisplayName.Trim();
		return new NormalizedSaveLinkInput(
			Index: index,
			Id: id,
			DisplayName: displayName,
			LocalPath: localPath,
			TargetPath: targetPath,
			EnsureOnLaunch: entry.EnsureOnLaunch);
	}

	private static void ValidateCrossLinkEndpointConflicts(IReadOnlyList<NormalizedSaveLinkInput> normalizedInputs)
	{
		var endpointConstraints = new List<SaveLinkEndpointConstraint>(normalizedInputs.Count * 2);
		foreach (var input in normalizedInputs)
		{
			EnsureNoCrossLinkPathConflict(input.Index, "ローカルパス", input.LocalPath, endpointConstraints);
			EnsureNoCrossLinkPathConflict(input.Index, "ターゲットパス", input.TargetPath, endpointConstraints);
			endpointConstraints.Add(new SaveLinkEndpointConstraint(input.Index, "ローカルパス", input.LocalPath));
			endpointConstraints.Add(new SaveLinkEndpointConstraint(input.Index, "ターゲットパス", input.TargetPath));
		}
	}

	private static GameSaveLinkItem ToGameSaveLinkItem(string gameId, NormalizedSaveLinkInput input)
	{
		return new GameSaveLinkItem(
			Id: input.Id,
			GameId: gameId,
			DisplayName: input.DisplayName,
			LocalPath: input.LocalPath,
			TargetPath: input.TargetPath,
			OrderNo: input.Index,
			EnsureOnLaunch: input.EnsureOnLaunch);
	}

	private static string BuildDefaultDisplayName(string localPath)
	{
		var directoryName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return string.IsNullOrWhiteSpace(directoryName) ? localPath : directoryName;
	}

	private static string? NormalizeAbsolutePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		try
		{
			var trimmedPath = path.Trim();
			if (!Path.IsPathFullyQualified(trimmedPath))
			{
				return null;
			}

			var normalized = Path.GetFullPath(trimmedPath);
			return Path.IsPathFullyQualified(normalized) ? normalized : null;
		}
		catch
		{
			return null;
		}
	}

	private static bool IsAncestorOrDescendantPath(string pathA, string pathB)
	{
		if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
		{
			return false;
		}

		return IsSameOrDescendant(pathA, pathB) || IsSameOrDescendant(pathB, pathA);
	}

	private static bool IsSameOrDescendant(string basePath, string candidatePath)
	{
		if (string.Equals(basePath, candidatePath, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		var normalizedBasePath = EnsureTrailingDirectorySeparator(basePath);
		return candidatePath.StartsWith(normalizedBasePath, StringComparison.OrdinalIgnoreCase);
	}

	private async Task<string?> TryRollbackUnlinkedLinksAsync(
		string gameId,
		IReadOnlyList<GameSaveLinkItem> unlinkedLinks)
	{
		if (unlinkedLinks.Count == 0)
		{
			return null;
		}

		var rollbackErrors = new List<string>();
		for (var index = unlinkedLinks.Count - 1; index >= 0; index--)
		{
			var link = unlinkedLinks[index];
			try
			{
				var restoreResult = await _localSaveLinkOperator.RestoreJunctionAsync(
					link.LocalPath,
					link.TargetPath,
					CancellationToken.None);
				if (restoreResult.IsSuccess)
				{
					continue;
				}

				rollbackErrors.Add($"link={link.Id}, reason={restoreResult.Message}");
				_logger.LogWarning(
					"Save-link rollback failed. gameId={GameId}, linkId={LinkId}, localPath={LocalPath}, targetPath={TargetPath}, reason={Reason}",
					gameId,
					link.Id,
					link.LocalPath,
					link.TargetPath,
					restoreResult.Message);
			}
			catch (Exception ex)
			{
				rollbackErrors.Add($"link={link.Id}, reason={ex.Message}");
				_logger.LogWarning(
					ex,
					"Save-link rollback failed unexpectedly. gameId={GameId}, linkId={LinkId}, localPath={LocalPath}, targetPath={TargetPath}",
					gameId,
					link.Id,
					link.LocalPath,
					link.TargetPath);
			}
		}

		return rollbackErrors.Count == 0
			? null
			: string.Join(" | ", rollbackErrors);
	}

	private static HashSet<string> BuildLinkEndpointKeySet(IReadOnlyList<GameSaveLinkItem>? links)
	{
		var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (links is null || links.Count == 0)
		{
			return result;
		}

		foreach (var link in links)
		{
			result.Add(BuildLinkEndpointKey(link.LocalPath, link.TargetPath));
		}

		return result;
	}

	private static string BuildLinkEndpointKey(string localPath, string targetPath)
	{
		var normalizedLocalPath = NormalizePathForComparison(localPath);
		var normalizedTargetPath = NormalizePathForComparison(targetPath);
		return $"{normalizedLocalPath}|{normalizedTargetPath}";
	}

	private static string NormalizePathForComparison(string path)
	{
		var normalized = NormalizeAbsolutePath(path) ?? path.Trim();
		return TrimTrailingDirectorySeparators(normalized);
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

	private static string EnsureTrailingDirectorySeparator(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return path;
		}

		return path.EndsWith(Path.DirectorySeparatorChar)
			? path
			: path + Path.DirectorySeparatorChar;
	}

	private sealed record SaveLinkEndpointConstraint(
		int LinkIndex,
		string Label,
		string Path);

	private sealed record NormalizedSaveLinkInput(
		int Index,
		string Id,
		string DisplayName,
		string LocalPath,
		string TargetPath,
		bool EnsureOnLaunch);
}
