using GameLauncherWithGit.Infrastructure.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface ILocalSaveLinkOperator
{
	Task<JunctionEnsureResult> EnsureJunctionAsync(
		string localPath,
		string targetPath,
		CancellationToken cancellationToken = default);

	Task<JunctionRemoveResult> RemoveJunctionWithRestoreAsync(
		string localPath,
		string targetPath,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Restores a removed link when the local path is absent, or accepts an existing matching link.
	/// An ordinary directory is left in place and reported as not restored.
	/// </summary>
	Task<JunctionEnsureResult> RestoreJunctionAsync(
		string localPath,
		string targetPath,
		CancellationToken cancellationToken = default);

	Task<DirectoryHydrationResult> HydrateDirectoryAsync(
		string targetPath,
		CancellationToken cancellationToken = default);
}
