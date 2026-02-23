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

	Task<DirectoryHydrationResult> HydrateDirectoryAsync(
		string targetPath,
		CancellationToken cancellationToken = default);
}
