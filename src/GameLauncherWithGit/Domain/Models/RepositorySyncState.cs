namespace GameLauncherWithGit.Domain.Models;

public enum RepositorySyncState
{
	Idle = 0,
	Debouncing = 1,
	Syncing = 2,
	ErrorPaused = 3
}
