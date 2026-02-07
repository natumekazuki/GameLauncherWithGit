using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Abstractions;

public interface IAppSettingsService
{
	AppSettings Get();

	Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
