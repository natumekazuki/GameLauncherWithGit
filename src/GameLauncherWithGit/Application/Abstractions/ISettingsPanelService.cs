namespace GameLauncherWithGit.Application.Abstractions;

public interface ISettingsPanelService
{
	event EventHandler? OpenRequested;

	void RequestOpen();
}
