namespace GameLauncherWithGit.Infrastructure.Models;

public sealed class RepositoryChangedEventArgs : EventArgs
{
	public RepositoryChangedEventArgs(string repositoryId)
	{
		RepositoryId = repositoryId;
	}

	public string RepositoryId { get; }
}
