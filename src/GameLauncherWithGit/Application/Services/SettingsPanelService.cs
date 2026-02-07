using GameLauncherWithGit.Application.Abstractions;

namespace GameLauncherWithGit.Application.Services;

public sealed class SettingsPanelService : ISettingsPanelService
{
	public event EventHandler? OpenRequested;

	public void RequestOpen()
	{
		OpenRequested?.Invoke(this, EventArgs.Empty);
	}
}
