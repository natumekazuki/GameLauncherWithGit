using GameLauncherWithGit.Domain.Models;

namespace GameLauncherWithGit.Application.Abstractions;

public interface IRepositoryStateStore
{
	IReadOnlyDictionary<string, RepositorySyncState> Snapshot();

	RepositorySyncState GetStateOrDefault(string repositoryId);

	void SetState(string repositoryId, RepositorySyncState state);
}
