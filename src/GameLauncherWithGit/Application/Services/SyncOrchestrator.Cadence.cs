using System.Globalization;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Domain.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;

namespace GameLauncherWithGit.Application.Services;

public sealed partial class SyncOrchestrator
{
	private static bool IsCadenceEnabled(AppSettings settings) =>
		settings.AutoCommitIntervalMinutes > 0 || settings.AutoPushIntervalMinutes > 0;

	private async Task<bool> IsRegisteredAsync(IGameLibraryStore library, string repositoryId, CancellationToken token)
	{
		var games = await library.GetAllAsync(token);
		return games.Any(game => game.RelatedRepositoryPath is { } path && !string.IsNullOrWhiteSpace(path)
			&& string.Equals(repositoryId, NormalizeRepositoryKey(path, _resolveDirectoryPath), StringComparison.OrdinalIgnoreCase));
	}

	internal static string NormalizeRepositoryKey(string path, Func<string, string>? resolveDirectoryPath)
	{
		var trimmed = path.Trim();
		try
		{
			// Preserve the normal Git error path if the existing directory identity cannot be resolved.
			if (resolveDirectoryPath is not null)
			{
				return resolveDirectoryPath(trimmed);
			}
		}
		catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
		{
		}

		try
		{
			return Path.IsPathFullyQualified(trimmed) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)) : trimmed;
		}
		catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
		{
			return trimmed;
		}
	}

	private async Task<bool> CheckRegisteredAsync(string repositoryId, DateTimeOffset startedAt, CancellationToken token)
	{
		try
		{
			var registered = _gameLibraryStore is null || await IsRegisteredAsync(_gameLibraryStore, repositoryId, token);
			token.ThrowIfCancellationRequested();
			return registered;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new SyncCommandException(repositoryId, "登録ゲームの照合", ex.Message, startedAt,
				shouldPauseRepository: false, isGitStep: false);
		}
	}

	// Only the current repository runner changes the work; the scheduler reads it after the runner finishes.
	private sealed class CadenceWork
	{
		public DateTimeOffset? CommitSince { get; set; }
		public DateTimeOffset? PushSince { get; set; }
		public DateTimeOffset? PushRetryAt { get; set; }
		public PushIdentity? PushIdentity { get; set; }
		public string? ConfirmedHead { get; set; }
		public int PushFailures { get; set; }
		public bool ReportPushRecovery { get; set; }
		public bool HasPending => CommitSince is not null || PushSince is not null;

		public DateTimeOffset? Next(AppSettings settings, DateTimeOffset? changeReadyAt)
		{
			var commit = CommitSince?.AddMinutes(settings.AutoCommitIntervalMinutes);
			if (changeReadyAt is { } ready && (commit is null
				|| (settings.AutoCommitIntervalMinutes == 0 && ready > commit)))
			{
				commit = ready;
			}
			var push = PushSince?.AddMinutes(settings.AutoPushIntervalMinutes);
			if (push is { } pushDue && PushRetryAt is { } retry && retry > pushDue)
			{
				push = retry;
			}

			return commit is null || (push is not null && push < commit) ? push : commit;
		}
	}

	private async Task<bool> ExecuteCadenceAsync(string repositoryId, CadenceWork work, AppSettings settings,
		bool force, DateTimeOffset? firstChangeAt, DateTimeOffset? commitNotBefore,
		DateTimeOffset startedAt, CancellationToken token)
	{
		var now = _clock.GetUtcNow();
		var dirty = await IsWorktreeDirtyAsync(repositoryId, startedAt, token);
		work.CommitSince = dirty ? work.CommitSince ?? firstChangeAt ?? now : null;

		var outgoing = await InspectOutgoingAsync(repositoryId, work, startedAt, token);
		UpdatePushWork(work, outgoing, now);

		var commitDue = work.CommitSince is { } commitSince
			&& (force || (commitSince.AddMinutes(settings.AutoCommitIntervalMinutes) <= now
				&& (settings.AutoCommitIntervalMinutes > 0 || commitNotBefore is null || commitNotBefore <= now)));
		var pushDue = IsPushDue(work, settings, force, now);
		if (!force && !commitDue && !pushDue)
		{
			return false;
		}

		if (force || commitDue || !dirty)
		{
			await PrepareIncomingAsync(repositoryId, startedAt, token);
			dirty = await IsWorktreeDirtyAsync(repositoryId, startedAt, token);
		}
		// An outgoing-only deadline must not stash/rebase new, still-debouncing worktree content.
		if ((force || commitDue) && dirty)
		{
			await CommitChangesAsync(repositoryId, startedAt, token);
			now = _clock.GetUtcNow();
			dirty = await IsWorktreeDirtyAsync(repositoryId, startedAt, token);
			work.CommitSince = dirty ? now : null;
		}
		else
		{
			work.CommitSince = dirty ? work.CommitSince ?? firstChangeAt ?? now : null;
		}

		// Pull/rebase or a new commit can change which commits still need pushing.
		outgoing = await InspectOutgoingAsync(repositoryId, work, startedAt, token);
		now = _clock.GetUtcNow();
		UpdatePushWork(work, outgoing, now);
		if (IsPushDue(work, settings, force, now) || (force && outgoing.Identity is not null))
		{
			work.PushSince ??= now;
			await PushCurrentBranchAsync(repositoryId, outgoing, startedAt, token);
			work.ConfirmedHead = outgoing.Head;
			work.ReportPushRecovery |= work.PushFailures > 0;
			work.PushSince = null;
			work.PushRetryAt = null;
			work.PushFailures = 0;
		}

		return !work.HasPending && outgoing.Identity is not null;
	}

	private static bool IsPushDue(CadenceWork work, AppSettings settings, bool force, DateTimeOffset now) =>
		work.PushSince is { } since && (force || (since.AddMinutes(settings.AutoPushIntervalMinutes) <= now
			&& (work.PushRetryAt is null || work.PushRetryAt <= now)));

	private static void UpdatePushWork(CadenceWork work, OutgoingCommits outgoing, DateTimeOffset now)
	{
		if (work.PushIdentity != outgoing.Identity)
		{
			work.PushIdentity = outgoing.Identity;
			work.ConfirmedHead = null;
			work.PushSince = null;
			work.PushRetryAt = null;
			work.PushFailures = 0;
			work.ReportPushRecovery = false;
		}
		if (!outgoing.NeedsConfirmation)
		{
			work.PushSince = null;
			work.PushRetryAt = null;
			work.PushFailures = 0;
			return;
		}

		work.PushSince ??= outgoing.Oldest is { } oldest && oldest < now ? oldest : now;
	}

	private async Task<bool> IsWorktreeDirtyAsync(string repositoryId, DateTimeOffset startedAt, CancellationToken token)
	{
		string[] command = ["status", "--porcelain"];
		var result = await ReadRequiredGitAsync(repositoryId, command, startedAt, token);
		foreach (var entry in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			if (entry.Length >= 2 && entry[..2] is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU")
			{
				throw new SyncCommandException(repositoryId, "status --porcelain",
					"未解決の競合があります。解消してから同期を再開してください。", startedAt, shouldPauseRepository: true);
			}
		}

		return !string.IsNullOrWhiteSpace(result.StandardOutput);
	}

	private sealed record PushIdentity(string SourceRef, string? Remote = null, string? DestinationRef = null,
		string? PushUrl = null, string? BlockedReason = null);
	private sealed record OutgoingCommits(PushIdentity? Identity, string? Head, bool NeedsConfirmation,
		DateTimeOffset? Oldest = null);

	private async Task<OutgoingCommits> InspectOutgoingAsync(string repositoryId, CadenceWork work,
		DateTimeOffset startedAt, CancellationToken token)
	{
		string[] branchCommand = ["symbolic-ref", "--quiet", "HEAD"];
		var branchResult = await RunGitAsync(repositoryId, branchCommand, token);
		if (!branchResult.IsSuccess || FirstNonEmptyLine(branchResult.StandardOutput) is not { } branch
			|| !branch.StartsWith("refs/heads/", StringComparison.Ordinal))
		{
			throw new SyncCommandException(repositoryId, "symbolic-ref --quiet HEAD",
				"送信する現在のブランチを特定できません。ブランチを選び直してください。", startedAt, shouldPauseRepository: true);
		}

		// Read the selected ref, not a second read of HEAD that could pick up a different branch.
		string[] headCommand = ["rev-parse", "--verify", "--quiet", branch];
		var head = await RunGitAsync(repositoryId, headCommand, token);
		if (IsMissingGitConfigValue(head))
		{
			return new(null, null, false);
		}
		RequireGitSuccess(repositoryId, headCommand, head, startedAt);
		var objectName = head.StandardOutput.Trim();
		if (objectName.Length is not (40 or 64) || !objectName.All(Uri.IsHexDigit))
		{
			throw new SyncCommandException(repositoryId, "rev-parse --verify --quiet",
				"送信するコミットを特定できません。", startedAt, shouldPauseRepository: false);
		}

		var identity = await ResolvePushIdentityAsync(repositoryId, branch, startedAt, token);
		var confirmed = work.PushIdentity == identity && work.ConfirmedHead == objectName;
		var target = identity.BlockedReason is null
			? await FindTrackingRefAsync(repositoryId, identity, startedAt, token) : null;
		if (target is null)
		{
			// A tracking ref for another URL or an unmapped ref says nothing about this destination.
			return new(identity, objectName, !confirmed);
		}
		string[] verifyTarget = ["show-ref", "--verify", "--quiet", target];
		var targetExists = await RunGitAsync(repositoryId, verifyTarget, token);
		if (IsMissingGitConfigValue(targetExists))
		{
			return new(identity, objectName, !confirmed);
		}
		RequireGitSuccess(repositoryId, verifyTarget, targetExists, startedAt);

		var range = $"{target}..{objectName}";
		if (work.PushIdentity == identity && work.PushSince is not null)
		{
			var ahead = await ReadRequiredGitAsync(repositoryId, ["rev-list", "--count", range], startedAt, token);
			if (int.TryParse(ahead.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
			{
				return new(identity, objectName, count > 0);
			}
			throw new SyncCommandException(repositoryId, "rev-list --count",
				"未送信コミットを確認できませんでした。", startedAt, shouldPauseRepository: false);
		}

		// Git retains commit times across app restarts; a dirty, uncommitted worktree has no equivalent Git timestamp.
		var dates = await ReadRequiredGitAsync(repositoryId, ["log", "--format=%ct", range], startedAt, token);
		long? earliest = null;
		foreach (var line in dates.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
				|| seconds < -62135596800 || seconds > 253402300799)
			{
				throw new SyncCommandException(repositoryId, "log --format=%ct",
					"未送信コミットの時刻を確認できませんでした。", startedAt, shouldPauseRepository: false);
			}
			earliest = earliest is { } previous ? Math.Min(previous, seconds) : seconds;
		}

		return earliest is { } value ? new(identity, objectName, true, DateTimeOffset.FromUnixTimeSeconds(value))
			: new(identity, objectName, false);
	}

	private async Task<PushIdentity> ResolvePushIdentityAsync(string repositoryId, string source,
		DateTimeOffset startedAt, CancellationToken token)
	{
		var branch = source["refs/heads/".Length..];
		var identity = new PushIdentity(source);
		const string ambiguous = "現在のブランチの送信先を一つに特定できません。Git の送信先設定を確認してください。";
		Task<string[]> Read(string key) => ReadConfigAsync(repositoryId, key, startedAt, token);
		var upstreamRemote = await Read($"branch.{branch}.remote");
		foreach (var key in new[] { $"branch.{branch}.pushRemote", "remote.pushDefault", $"branch.{branch}.remote" })
		{
			var names = await Read(key);
			if (names.Length == 0) continue;
			if (names is not [var name] || string.IsNullOrWhiteSpace(name) || name == ".")
				return identity with { BlockedReason = ambiguous };
			identity = identity with { Remote = name };
			break;
		}
		var remote = identity.Remote ?? "origin";
		identity = identity with { Remote = remote };
		var urls = await RunGitAsync(repositoryId, ["remote", "get-url", "--push", "--all", "--", remote], token);
		if (!urls.IsSuccess || ReadLines(urls.StandardOutput) is not [var url] || string.IsNullOrWhiteSpace(url))
			return identity with { BlockedReason = ambiguous };
		identity = identity with { PushUrl = url };

		var mirror = await ReadConfigAsync(repositoryId, $"remote.{remote}.mirror", startedAt, token, boolean: true);
		if (mirror.Length != 0 && mirror is not ["false"])
			return identity with { BlockedReason = "ミラー送信は自動同期では使えません。Git の送信先設定を確認してください。" };

		string destination;
		var specs = await Read($"remote.{remote}.push");
		if (specs.Length != 0)
		{
			if (specs is not [var spec] || spec.Split(':') is not [var from, var to])
				return identity with { BlockedReason = ambiguous };
			if (from != source)
			{
				if (from != branch && from != "HEAD") return identity with { BlockedReason = ambiguous };
				var resolved = await RunGitAsync(repositoryId,
					["rev-parse", "--symbolic-full-name", "--verify", "--end-of-options", from], token);
				if (!resolved.IsSuccess || resolved.StandardOutput.TrimEnd('\r', '\n') != source)
					return identity with { BlockedReason = ambiguous };
			}
			destination = to;
		}
		else
		{
			var defaults = await Read("push.default");
			if (defaults.Length > 1) return identity with { BlockedReason = ambiguous };
			var mode = defaults.FirstOrDefault() ?? "simple";
			destination = source;
			switch (mode)
			{
				case "current":
					break;
				case "simple" when upstreamRemote is [var fetchRemote] && fetchRemote != remote:
					break;
				case "upstream":
				case "simple":
					var merge = await Read($"branch.{branch}.merge");
					if (upstreamRemote is not [var configured] || configured != remote || merge is not [var upstream]
						|| (mode == "simple" && upstream != source))
						return identity with { BlockedReason = ambiguous };
					destination = upstream;
					break;
				default:
					return identity with { BlockedReason = ambiguous };
			}
		}
		if (!destination.StartsWith("refs/heads/", StringComparison.Ordinal)
			|| !(await RunGitAsync(repositoryId, ["check-ref-format", destination], token)).IsSuccess)
			return identity with { BlockedReason = ambiguous };
		return identity with { DestinationRef = destination };
	}

	private async Task<string[]> ReadConfigAsync(string repositoryId, string key, DateTimeOffset startedAt,
		CancellationToken token, bool boolean = false)
	{
		string[] command = boolean ? ["config", "--null", "--type=bool", "--get-all", key] : ["config", "--null", "--get-all", key];
		var result = await RunGitAsync(repositoryId, command, token);
		if (IsMissingGitConfigValue(result)) return [];
		RequireGitSuccess(repositoryId, command, result, startedAt);
		return result.StandardOutput.EndsWith('\0') ? result.StandardOutput[..^1].Split('\0') : [result.StandardOutput];
	}

	private static string[] ReadLines(string output)
	{
		var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal);
		return (normalized.EndsWith('\n') ? normalized[..^1] : normalized).Split('\n');
	}

	private async Task<string?> FindTrackingRefAsync(string repositoryId, PushIdentity identity,
		DateTimeOffset startedAt, CancellationToken token)
	{
		var fetch = await RunGitAsync(repositoryId, ["remote", "get-url", "--all", "--", identity.Remote!], token);
		var fetchUrls = ReadLines(fetch.StandardOutput);
		if (!fetch.IsSuccess || fetchUrls.Length != 1 || fetchUrls[0] != identity.PushUrl)
		{
			return null;
		}

		var specs = await ReadConfigAsync(repositoryId, $"remote.{identity.Remote}.fetch", startedAt, token);
		// Only infer tracking from a single positive fetch mapping. Multiple/negative mappings are not proof.
		if (specs is not [var spec] || spec.StartsWith('^')) return null;
		var fields = spec.TrimStart('+').Split(':');
		if (fields.Length != 2 || !fields[1].StartsWith("refs/remotes/", StringComparison.Ordinal)) return null;
		if (fields[0] == identity.DestinationRef && !fields[1].Contains('*')) return fields[1];
		if (!fields[0].EndsWith("/*", StringComparison.Ordinal) || !fields[1].EndsWith("/*", StringComparison.Ordinal)) return null;
		var prefix = fields[0][..^1];
		return identity.DestinationRef!.StartsWith(prefix, StringComparison.Ordinal) ? fields[1][..^1] + identity.DestinationRef[prefix.Length..] : null;
	}

	private async Task PushCurrentBranchAsync(string repositoryId, OutgoingCommits outgoing,
		DateTimeOffset startedAt, CancellationToken token)
	{
		var target = outgoing.Identity!;
		if (target.BlockedReason is { } reason)
		{
			throw new SyncCommandException(repositoryId, "push", reason, startedAt,
				shouldPauseRepository: false, isPushStep: true);
		}
		var branch = await RunGitAsync(repositoryId, ["symbolic-ref", "--quiet", "HEAD"], token);
		if (!branch.IsSuccess || FirstNonEmptyLine(branch.StandardOutput) != target.SourceRef)
		{
			throw new SyncCommandException(repositoryId, "push",
				"送信元ブランチが切り替わったため送信を見送りました。", startedAt,
				shouldPauseRepository: false, isPushStep: true);
		}

		var result = await RunGitAsync(repositoryId, ["push", "--porcelain", "--no-force", "--no-mirror",
			"--no-tags", "--no-follow-tags", "--recurse-submodules=no", "--", target.Remote!,
			$"{outgoing.Head}:{target.DestinationRef}"], token);
		if (result.IsSuccess && result.StandardOutput.Split('\n').Any(line =>
		{
			if (line.Length < 3 || line[1] != '\t' || line[0] is not (' ' or '*' or '='))
			{
				return false;
			}
			var endOfSource = line.IndexOf(':', 2);
			var endOfRefSpec = line.IndexOf('\t', 2);
			return endOfSource > 2 && endOfRefSpec > endOfSource
				&& line[2..endOfSource] == outgoing.Head
				&& line[(endOfSource + 1)..endOfRefSpec] == target.DestinationRef;
		}))
		{
			return;
		}

		throw new SyncCommandException(repositoryId, "push --porcelain",
			result.IsSuccess ? "予定した送信先への反映を確認できません。Git の送信先設定を確認してください。" : BuildFailureReason(result),
			startedAt, shouldPauseRepository: false, isPushStep: true);
	}

	private async Task<GitCommandResult> ReadRequiredGitAsync(string repositoryId, string[] command,
		DateTimeOffset startedAt, CancellationToken token)
	{
		var result = await RunGitAsync(repositoryId, command, token);
		RequireGitSuccess(repositoryId, command, result, startedAt);
		return result;
	}

	private static void RequireGitSuccess(string repositoryId, string[] command, GitCommandResult result, DateTimeOffset startedAt)
	{
		if (!result.IsSuccess)
		{
			throw new SyncCommandException(repositoryId, string.Join(' ', command), BuildFailureReason(result), startedAt,
				shouldPauseRepository: false);
		}
	}

	private static void CancelCadenceWakeLocked(RepositorySyncQueue queue)
	{
		var wake = queue.CadenceCts;
		queue.CadenceCts = null;
		wake?.Cancel();
	}

	private sealed record CadenceWake(TimeSpan Delay, CancellationTokenSource Source, CancellationToken Token);

	// Called by the active runner before relinquishing its queue lock.
	private CadenceWake? ReserveCadenceWakeLocked(string repositoryId, RepositorySyncQueue queue, AppSettings settings)
	{
		if (_isDisposed || queue.RetryCts is not null
			|| _repositoryStateStore.GetStateOrDefault(repositoryId) == RepositorySyncState.ErrorPaused)
		{
			return null;
		}

		CancelCadenceWakeLocked(queue);
		if (queue.Cadence.Next(settings, queue.ChangeReadyAt) is not { } next) return null;
		var wake = new CancellationTokenSource();
		queue.CadenceCts = wake;
		return new(next - _clock.GetUtcNow(), wake, wake.Token);
	}

	private async Task WakeAtDeadlineAsync(string repositoryId, RepositorySyncQueue queue, TimeSpan delay,
		CancellationTokenSource wake, CancellationToken token)
	{
		try
		{
			// Yield for a due-now deadline; do not recurse through a synchronous Git adapter.
			await _delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1), token);
			SyncReservation? reservation;
			lock (queue.Gate)
			{
				if (!ReferenceEquals(queue.CadenceCts, wake) || token.IsCancellationRequested)
				{
					return;
				}

				queue.CadenceCts = null;
				if (!TryReserveLocked(repositoryId, queue, SyncRequest.Due, CancellationToken.None, out reservation))
				{
					return;
				}
			}

			StartReservation(repositoryId, queue, reservation);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		finally
		{
			lock (queue.Gate)
			{
				if (ReferenceEquals(queue.CadenceCts, wake))
				{
					queue.CadenceCts = null;
				}
				wake.Dispose();
			}
		}
	}
}
