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

		var normalized = new List<GameSaveLinkItem>(links.Count);
		var endpointConstraints = new List<SaveLinkEndpointConstraint>(links.Count * 2);
		for (var index = 0; index < links.Count; index++)
		{
			var entry = links[index];
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

			EnsureNoCrossLinkPathConflict(index, "ローカルパス", localPath, endpointConstraints);
			EnsureNoCrossLinkPathConflict(index, "ターゲットパス", targetPath, endpointConstraints);

			var id = string.IsNullOrWhiteSpace(entry.Id)
				? $"save-link-{Guid.NewGuid():N}"
				: entry.Id.Trim();
			var displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
				? BuildDefaultDisplayName(localPath)
				: entry.DisplayName.Trim();
			normalized.Add(
				new GameSaveLinkItem(
					Id: id,
					GameId: gameId,
					DisplayName: displayName,
					LocalPath: localPath,
					TargetPath: targetPath,
					OrderNo: index,
					EnsureOnLaunch: entry.EnsureOnLaunch));

			endpointConstraints.Add(new SaveLinkEndpointConstraint(index, "ローカルパス", localPath));
			endpointConstraints.Add(new SaveLinkEndpointConstraint(index, "ターゲットパス", targetPath));
		}

		return normalized;
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
}
