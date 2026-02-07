using GameLauncherWithGit.Domain.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface ITrayService
{
	void SetState(RepositorySyncState state);
}
