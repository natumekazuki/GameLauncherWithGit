namespace GameLauncherWithGit.App.Infrastructure.Abstractions;

public interface INotificationService
{
    Task NotifyInfoAsync(string title, string message, CancellationToken cancellationToken = default);

    Task NotifyErrorAsync(string title, string message, CancellationToken cancellationToken = default);
}
