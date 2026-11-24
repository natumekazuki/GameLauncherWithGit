using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Application.Abstractions;

public interface IAppSettingsService
{
	AppSettings Get();

	/// <summary>
	/// Saves the normalized settings. If the write fails, the current settings are unchanged.
	/// </summary>
	Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
