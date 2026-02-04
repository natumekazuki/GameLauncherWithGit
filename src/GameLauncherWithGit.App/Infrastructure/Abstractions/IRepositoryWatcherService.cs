using GameLauncherWithGit.App.Infrastructure.Models;

namespace GameLauncherWithGit.App.Infrastructure.Abstractions;

public interface IRepositoryWatcherService
{
    event EventHandler<RepositoryChangedEventArgs>? RepositoryChanged;

    void StartWatch(string repositoryId, string path, int debounceSeconds);

    void StopWatch(string repositoryId);
}
