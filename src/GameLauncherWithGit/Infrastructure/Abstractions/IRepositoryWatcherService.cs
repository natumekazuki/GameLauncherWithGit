using GameLauncherWithGit.Infrastructure.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface IRepositoryWatcherService
{
	event EventHandler<RepositoryChangedEventArgs>? RepositoryChanged;

	/// <summary>Raised when a repository first becomes watched, including after a previous registration was removed.</summary>
	event EventHandler<RepositoryChangedEventArgs>? RepositoryRegistered;

	void Register(string repositoryId, string watchPath);

	void Unregister(string repositoryId);
}
