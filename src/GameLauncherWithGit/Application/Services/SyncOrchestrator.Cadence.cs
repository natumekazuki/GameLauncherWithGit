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
		public bool HasPending => CommitSince is not null || PushSince is not null;

		public DateTimeOffset? Next(AppSettings settings)
		{
			var commit = CommitSince?.AddMinutes(settings.AutoCommitIntervalMinutes);
			var push = PushSince?.AddMinutes(settings.AutoPushIntervalMinutes);
			if (push is { } pushDue && PushRetryAt is { } retry && retry > pushDue)
			{
				push = retry;
			}

			return commit is null || (push is not null && push < commit) ? push : commit;
		}
	}

	private async Task<bool> ExecuteCadenceAsync(string repositoryId, CadenceWork work, AppSettings settings,
		bool force, DateTimeOffset? firstChangeAt, DateTimeOffset startedAt, CancellationToken token)
	{
		var now = _clock.GetUtcNow();
		var dirty = await IsWorktreeDirtyAsync(repositoryId, startedAt, token);
		work.CommitSince = dirty ? work.CommitSince ?? firstChangeAt ?? now : null;

		var outgoing = await InspectOutgoingAsync(repositoryId, work.PushSince is null, startedAt, token);
		UpdatePushWork(work, outgoing, now);

		var commitDue = work.CommitSince is { } commitSince
			&& (force || commitSince.AddMinutes(settings.AutoCommitIntervalMinutes) <= now);
		var pushDue = IsPushDue(work, settings, force, now);
		if (!force && !commitDue && !pushDue)
		{
			return false;
		}

		await PrepareIncomingAsync(repositoryId, startedAt, token);
		dirty = await IsWorktreeDirtyAsync(repositoryId, startedAt, token);
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
		outgoing = await InspectOutgoingAsync(repositoryId, includeAge: false, startedAt, token);
		now = _clock.GetUtcNow();
		UpdatePushWork(work, outgoing, now);
		if (IsPushDue(work, settings, force, now) || (force && outgoing.Branch is not null))
		{
			work.PushSince ??= now;
			await PushCurrentBranchAsync(repositoryId, outgoing.Branch!, startedAt, token);
			work.PushSince = null;
			work.PushRetryAt = null;
		}

		return !work.HasPending && outgoing.Branch is not null;
	}

	private static bool IsPushDue(CadenceWork work, AppSettings settings, bool force, DateTimeOffset now) =>
		work.PushSince is { } since && (force || (since.AddMinutes(settings.AutoPushIntervalMinutes) <= now
			&& (work.PushRetryAt is null || work.PushRetryAt <= now)));

	private static void UpdatePushWork(CadenceWork work, OutgoingCommits outgoing, DateTimeOffset now)
	{
		if (!outgoing.NeedsConfirmation)
		{
			work.PushSince = null;
			work.PushRetryAt = null;
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

	private sealed record OutgoingCommits(string? Branch, bool NeedsConfirmation, DateTimeOffset? Oldest = null);

	private async Task<OutgoingCommits> InspectOutgoingAsync(string repositoryId, bool includeAge,
		DateTimeOffset startedAt, CancellationToken token)
	{
		string[] headCommand = ["rev-parse", "--verify", "--quiet", "HEAD"];
		var head = await RunGitAsync(repositoryId, headCommand, token);
		if (IsMissingGitConfigValue(head))
		{
			return new(null, false);
		}
		RequireGitSuccess(repositoryId, headCommand, head, startedAt);
		string[] branchCommand = ["symbolic-ref", "--quiet", "HEAD"];
		var branchResult = await RunGitAsync(repositoryId, branchCommand, token);
		if (!branchResult.IsSuccess || FirstNonEmptyLine(branchResult.StandardOutput) is not { } branch
			|| !branch.StartsWith("refs/heads/", StringComparison.Ordinal))
		{
			throw new SyncCommandException(repositoryId, "symbolic-ref --quiet HEAD",
				"送信する現在のブランチを特定できません。ブランチを選び直してください。", startedAt, shouldPauseRepository: true);
		}

		var targetResult = await ReadRequiredGitAsync(repositoryId,
			["for-each-ref", "--format=%(push)%09%(push:remotename)", branch], startedAt, token);
		var destination = targetResult.StandardOutput.TrimEnd('\r', '\n').Split('\t');
		if (destination.Length != 2 || !destination[0].StartsWith("refs/", StringComparison.Ordinal)
			|| string.IsNullOrWhiteSpace(destination[1])
			|| !await SharesTrackingRemoteAsync(repositoryId, destination[1], token))
		{
			// A missing local push ref is inconclusive, not zero outgoing commits.
			return new(branch, true);
		}

		var target = destination[0];
		string[] verifyTarget = ["show-ref", "--verify", "--quiet", target];
		var targetExists = await RunGitAsync(repositoryId, verifyTarget, token);
		if (IsMissingGitConfigValue(targetExists))
		{
			return new(branch, true);
		}
		RequireGitSuccess(repositoryId, verifyTarget, targetExists, startedAt);

		var range = $"{target}..HEAD";
		if (!includeAge)
		{
			var ahead = await ReadRequiredGitAsync(repositoryId, ["rev-list", "--count", range], startedAt, token);
			if (int.TryParse(ahead.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
			{
				return new(branch, count > 0);
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

		return earliest is { } value ? new(branch, true, DateTimeOffset.FromUnixTimeSeconds(value)) : new(branch, false);
	}

	private async Task<bool> SharesTrackingRemoteAsync(string repositoryId, string remote, CancellationToken token)
	{
		// A tracking ref reflects a fetch URL. A distinct push URL or multiple targets need confirmation by Git push itself.
		var fetch = await RunGitAsync(repositoryId, ["remote", "get-url", "--all", "--", remote], token);
		var push = await RunGitAsync(repositoryId, ["remote", "get-url", "--push", "--all", "--", remote], token);
		var fetchUrls = fetch.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var pushUrls = push.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return fetch.IsSuccess && push.IsSuccess && fetchUrls.Length == 1 && pushUrls.Length == 1
			&& string.Equals(fetchUrls[0], pushUrls[0], StringComparison.Ordinal);
	}

	private async Task PushCurrentBranchAsync(string repositoryId, string branch, DateTimeOffset startedAt, CancellationToken token)
	{
		var result = await RunGitAsync(repositoryId, ["push", "--porcelain"], token);
		if (result.IsSuccess && result.StandardOutput.Split('\n').Any(line =>
		{
			if (line.Length < 3 || line[1] != '\t' || line[0] is not (' ' or '+' or '*' or '='))
			{
				return false;
			}
			var endOfSource = line.IndexOf(':', 2);
			return endOfSource > 2 && (line[2..endOfSource] == branch || line[2..endOfSource] == "HEAD");
		}))
		{
			return;
		}

		throw new SyncCommandException(repositoryId, "push --porcelain",
			result.IsSuccess ? "現在のブランチの送信を確認できません。Git の送信先設定を確認してください。" : BuildFailureReason(result),
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

	private void ScheduleCadenceWake(string repositoryId, RepositorySyncQueue queue, AppSettings settings)
	{
		CancellationTokenSource wake;
		CancellationToken token;
		TimeSpan delay;
		lock (queue.Gate)
		{
			if (_isDisposed || queue.IsSyncRunning || queue.RetryCts is not null
				|| _repositoryStateStore.GetStateOrDefault(repositoryId) == RepositorySyncState.ErrorPaused)
			{
				return;
			}

			CancelCadenceWakeLocked(queue);
			queue.UsesCadence = IsCadenceEnabled(settings) || queue.Cadence.HasPending;
			if (queue.Cadence.Next(settings) is not { } next)
			{
				return;
			}

			delay = next - _clock.GetUtcNow();
			wake = new CancellationTokenSource();
			token = wake.Token;
			queue.CadenceCts = wake;
		}

		_ = WakeAtDeadlineAsync(repositoryId, queue, delay, wake, token);
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
