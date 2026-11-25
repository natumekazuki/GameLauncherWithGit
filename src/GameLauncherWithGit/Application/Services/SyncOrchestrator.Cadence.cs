using System.Globalization;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Domain.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.Models;
using GameLauncherWithGit.Infrastructure.Services;
using Microsoft.Extensions.Logging;

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
		private readonly Dictionary<PushTargetKey, PushWork> _selections = [];
		public DateTimeOffset? CommitSince { get; set; }
		public PushWork? SelectedPush { get; private set; }
		public bool HasPending => CommitSince is not null || SelectedPush?.Since is not null;

		public PushWork? Select(PushIdentity? identity)
		{
			if (identity is null) return SelectedPush = null;
			var key = new PushTargetKey(identity.SourceRef, identity.PushUrl, identity.DestinationRef, identity.BlockedReason);
			if (!_selections.TryGetValue(key, out var push))
			{
				// Only the first selection can reconstruct a pre-startup interval. A different
				// selection starts when observed; returning to a known one uses its existing deadline.
				push = new PushWork { RestoreCommitAge = _selections.Count == 0 };
				_selections.Add(key, push);
			}
			return SelectedPush = push;
		}

		public DateTimeOffset? Next(AppSettings settings, DateTimeOffset? changeReadyAt)
		{
			var commit = CommitSince?.AddMinutes(settings.AutoCommitIntervalMinutes);
			if (changeReadyAt is { } ready && (commit is null
				|| (settings.AutoCommitIntervalMinutes == 0 && ready > commit)))
			{
				commit = ready;
			}
			var push = SelectedPush?.Since?.AddMinutes(settings.AutoPushIntervalMinutes);
			if (push is { } pushDue && SelectedPush?.RetryAt is { } retry && retry > pushDue)
			{
				push = retry;
			}

			return commit is null || (push is not null && push < commit) ? push : commit;
		}
	}

	private sealed class PushWork
	{
		public bool RestoreCommitAge { get; set; }
		public bool EndpointInspected { get; set; }
		public bool EndpointKnown { get; set; }
		public string? EndpointTip { get; set; }
		public DateTimeOffset? Since { get; set; }
		public DateTimeOffset? RetryAt { get; set; }
		public int Failures { get; set; }
		public bool ReportRecovery { get; set; }
	}

	private async Task<bool> ExecuteCadenceAsync(string repositoryId, CadenceWork work, AppSettings settings,
		bool force, DateTimeOffset? firstChangeAt, Func<bool> mayAcquireWorktree,
		DateTimeOffset startedAt, CancellationToken token)
	{
		var dirty = await IsWorktreeDirtyAsync(repositoryId, startedAt, token);
		var now = _clock.GetUtcNow();
		work.CommitSince = dirty ? work.CommitSince ?? firstChangeAt ?? now : null;

		var outgoing = await InspectOutgoingAsync(repositoryId, work, startedAt, token);
		now = _clock.GetUtcNow();
		UpdatePushWork(work.SelectedPush, outgoing, now);

		var commitDue = work.CommitSince is { } commitSince
			&& (force || (commitSince.AddMinutes(settings.AutoCommitIntervalMinutes) <= now && mayAcquireWorktree()));
		var pushDue = IsPushDue(work.SelectedPush, settings, force, now);
		if (!force && !commitDue && !pushDue)
		{
			return false;
		}

		// With no advertised branch to pull, an outgoing-only sync can create it using
		// the verified, non-force endpoint command. Local commits still use the incoming path.
		if ((force || commitDue || (!dirty && work.SelectedPush?.EndpointTip is not null)) && mayAcquireWorktree())
		{
			await PrepareIncomingAsync(repositoryId, startedAt, token, mayAcquireWorktree);
			dirty = await IsWorktreeDirtyAsync(repositoryId, startedAt, token);
		}
		// An outgoing-only deadline must not stash/rebase new, still-debouncing worktree content.
		if ((force || commitDue) && dirty && mayAcquireWorktree())
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
		var push = work.SelectedPush;
		UpdatePushWork(push, outgoing, now);
		if (push is not null && (IsPushDue(push, settings, force, now) || force))
		{
			var tracking = IsCadenceEnabled(settings) && outgoing.Identity is { BlockedReason: null } target
				? await ReadTrackingPlanAsync(repositoryId, target, startedAt, token) : null;
			push.Since ??= now;
			await PushCurrentBranchAsync(repositoryId, outgoing, startedAt, token);
			push.EndpointKnown = true;
			push.EndpointTip = outgoing.Head;
			push.ReportRecovery |= push.Failures > 0;
			push.Since = null;
			push.RetryAt = null;
			push.Failures = 0;
			if (tracking is not null)
			{
				await RefreshTrackingAfterPushAsync(repositoryId, outgoing.Identity!, tracking, startedAt, token);
			}
		}

		return !work.HasPending && outgoing.Identity is not null && mayAcquireWorktree();
	}

	private static bool IsPushDue(PushWork? push, AppSettings settings, bool force, DateTimeOffset now) =>
		push?.Since is { } since && (force || (since.AddMinutes(settings.AutoPushIntervalMinutes) <= now
			&& (push.RetryAt is null || push.RetryAt <= now)));

	private static void UpdatePushWork(PushWork? push, OutgoingCommits outgoing, DateTimeOffset now)
	{
		if (push is null) return;
		if (!outgoing.NeedsConfirmation)
		{
			push.Since = null;
			push.RetryAt = null;
			push.Failures = 0;
			return;
		}

		push.Since ??= outgoing.Oldest is { } oldest && oldest < now ? oldest : now;
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
	// Names select the current configuration, but an alias for the same endpoint owns the same deadline.
	private sealed record PushTargetKey(string SourceRef, string? PushUrl, string? DestinationRef, string? BlockedReason);
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
			work.Select(null);
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
		var push = work.Select(identity)!;
		if (!push.EndpointInspected && identity.BlockedReason is null)
		{
			// A tracking ref is not evidence for the configured URL. Read the actual destination
			// once per selection, then use that observation and successful pushes without polling.
			var advertised = await _gitService.RunCadenceEndpointAsync(repositoryId, identity.PushUrl!,
				identity.DestinationRef!, commit: null, token);
			token.ThrowIfCancellationRequested();
			push.EndpointInspected = true;
			push.EndpointKnown = advertised.IsSuccess;
			push.EndpointTip = advertised.IsSuccess && advertised.StandardOutput.Length > 0
				? advertised.StandardOutput : null;
		}
		var restoreAge = push.RestoreCommitAge;
		push.RestoreCommitAge = false;
		if (!push.EndpointKnown)
		{
			return new(identity, objectName, true);
		}
		var target = push.EndpointTip;
		if (target == objectName) return new(identity, objectName, false);
		if (target is not null)
		{
			var knownLocally = await RunGitAsync(repositoryId, ["rev-parse", "--verify", "--quiet", target + "^{commit}"], token);
			if (!knownLocally.IsSuccess) return new(identity, objectName, true);
		}

		var range = target is null ? objectName : $"{target}..{objectName}";
		if (!restoreAge)
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
			if (names is not [var name] || string.IsNullOrWhiteSpace(name) || name == "."
				|| name.IndexOfAny([':', '@', '\r', '\n', '\0']) >= 0)
				return identity with { BlockedReason = ambiguous };
			identity = identity with { Remote = name };
			break;
		}
		var remote = identity.Remote;
		if (remote is null)
		{
			var remotes = await ReadRequiredGitAsync(repositoryId, ["remote"], startedAt, token);
			remote = GitService.DefaultCadenceRemote(remotes.StandardOutput);
		}
		identity = identity with { Remote = remote };
		var urls = await _gitService.ReadCadencePushUrlsAsync(repositoryId, remote, token);
		token.ThrowIfCancellationRequested();
		if (!urls.IsSuccess || ReadLines(urls.StandardOutput) is not [var url] || string.IsNullOrWhiteSpace(url))
			return identity with { BlockedReason = ambiguous };
		if (GitService.HasCadenceEmbeddedHttpCredentials(url))
			return identity with { BlockedReason = GitService.CadenceEmbeddedCredentialsMessage };
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

	private sealed record TrackingPlan(string LocalRef, string FetchRefspec);

	private async Task<TrackingPlan?> ReadTrackingPlanAsync(string repositoryId, PushIdentity target,
		DateTimeOffset startedAt, CancellationToken token)
	{
		try
		{
			var remote = target.Remote!;
			var fetchUrls = await _gitService.ReadCadenceFetchUrlsAsync(repositoryId, remote, token);
			token.ThrowIfCancellationRequested();
			if (!fetchUrls.IsSuccess || ReadLines(fetchUrls.StandardOutput) is not [var url] || url != target.PushUrl)
				return null;

			var branch = target.SourceRef["refs/heads/".Length..];
			if (await ReadConfigAsync(repositoryId, $"branch.{branch}.remote", startedAt, token) is not [var upstreamRemote]
				|| upstreamRemote != remote
				|| await ReadConfigAsync(repositoryId, $"branch.{branch}.merge", startedAt, token) is not [var upstreamRef]
				|| upstreamRef != target.DestinationRef
				|| await ReadConfigAsync(repositoryId, $"remote.{remote}.fetch", startedAt, token) is not [var fetchRefspec]
				|| GetTrackingRef(fetchRefspec, upstreamRef) is not { } trackingRef)
				return null;

			var validated = await RunGitAsync(repositoryId, ["check-ref-format", trackingRef], token);
			if (!validated.IsSuccess) return null;
			var symbolic = await RunGitAsync(repositoryId, ["symbolic-ref", "--quiet", "--no-recurse", trackingRef], token);
			if (!IsMissingGitConfigValue(symbolic)) return null; // Do not write through a symbolic tracking ref.

			var selected = await RunGitAsync(repositoryId, ["symbolic-ref", "--quiet", "HEAD"], token);
			return selected.IsSuccess && FirstNonEmptyLine(selected.StandardOutput) == target.SourceRef
				? new(trackingRef, fetchRefspec) : null;
		}
		catch (Exception) when (!token.IsCancellationRequested)
		{
			// Unknown tracking is not a reason to delay or block the send itself.
			return null;
		}
	}

	private static string? GetTrackingRef(string fetchRefspec, string remoteRef)
	{
		var spec = fetchRefspec.StartsWith('+') ? fetchRefspec[1..] : fetchRefspec;
		if (spec.Split(':') is not [var from, var to] || !to.StartsWith("refs/remotes/", StringComparison.Ordinal))
			return null;
		if (from == remoteRef && !to.Contains('*')) return to;
		if (from.EndsWith("/*", StringComparison.Ordinal) && to.EndsWith("/*", StringComparison.Ordinal)
			&& !from[..^1].Contains('*') && !to[..^1].Contains('*')
			&& remoteRef.StartsWith(from[..^1], StringComparison.Ordinal))
			return to[..^1] + remoteRef[(from.Length - 1)..];
		return null;
	}

	private async Task RefreshTrackingAfterPushAsync(string repositoryId, PushIdentity sentTarget,
		TrackingPlan beforePush, DateTimeOffset startedAt, CancellationToken token)
	{
		// A confirmed send is already complete. Refresh only the same configured mapping;
		// a skipped/failed fetch never enters the queue's failure or retry path.
		try
		{
			if (await ResolvePushIdentityAsync(repositoryId, sentTarget.SourceRef, startedAt, token) != sentTarget
				|| await ReadTrackingPlanAsync(repositoryId, sentTarget, startedAt, token) != beforePush)
				return;

			var refreshed = await _gitService.FetchCadenceTrackingAsync(repositoryId, sentTarget.PushUrl!,
				sentTarget.DestinationRef!, beforePush.LocalRef, token);
			if (refreshed.IsSuccess || token.IsCancellationRequested) return;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			return;
		}
		catch (Exception)
		{
			// Never include an exception or raw transport output in the post-push warning.
		}
		_logger.LogWarning("Git confirmed the push but could not refresh its tracking ref. repositoryId={RepositoryId}", repositoryId);
	}

	private async Task PushCurrentBranchAsync(string repositoryId, OutgoingCommits outgoing,
		DateTimeOffset startedAt, CancellationToken token)
	{
		var target = outgoing.Identity!;
		var commit = outgoing.Head ?? throw new InvalidOperationException("A push requires a selected commit.");
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

		var result = await _gitService.RunCadenceEndpointAsync(repositoryId, target.PushUrl!, target.DestinationRef!, commit, token);
		token.ThrowIfCancellationRequested();
		if (result.IsSuccess) return;
		throw new SyncCommandException(repositoryId, "push --porcelain",
			BuildFailureReason(result),
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
