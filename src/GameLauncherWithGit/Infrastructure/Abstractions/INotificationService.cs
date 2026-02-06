namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface INotificationService
{
	Task NotifyAsync(
		string title,
		string message,
		CancellationToken cancellationToken = default);
}
