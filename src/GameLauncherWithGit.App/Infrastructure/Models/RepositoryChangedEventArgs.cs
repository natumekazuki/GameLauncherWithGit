namespace GameLauncherWithGit.App.Infrastructure.Models;

public sealed class RepositoryChangedEventArgs : EventArgs
{
    public RepositoryChangedEventArgs(string repositoryId, string path)
    {
        RepositoryId = repositoryId;
        Path = path;
    }

    public string RepositoryId { get; }

    public string Path { get; }
}
