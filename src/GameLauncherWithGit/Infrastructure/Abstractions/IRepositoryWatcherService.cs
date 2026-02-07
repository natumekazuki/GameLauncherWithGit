using GameLauncherWithGit.Infrastructure.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IRepositoryWatcherService
{
	event EventHandler<RepositoryChangedEventArgs>? RepositoryChanged;

	void Register(string repositoryId, string watchPath);

	void Unregister(string repositoryId);
}
