namespace GameLauncherWithGit.Application.Abstractions;

public interface ISyncOrchestrator
{
	/// <summary>Queues a debounced sync unless the repository is paused after an error.</summary>
	Task QueueRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Requests a cadence check after a successful branch switch in the same worktree.
	/// Does not bypass the change debounce, enable cadence, or resume an error-paused repository.
	/// </summary>
	Task NotifyRepositoryBranchChangedAsync(string repositoryId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Requests sync without debounce, without resuming an error-paused repository.
	/// Returns whether the request was accepted, not whether sync succeeded.
	/// </summary>
	bool TryQueueRepositorySyncNow(string repositoryId);

	/// <summary>Explicitly resumes a repository after error recovery; completion is tracked separately.</summary>
	Task ResumeRepositorySyncAsync(string repositoryId, CancellationToken cancellationToken = default);
}
