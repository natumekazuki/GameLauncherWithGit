using GameLauncherWithGit.App.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.App.Infrastructure.Services;

public sealed class NotificationService : INotificationService
{
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(ILogger<NotificationService> logger)
    {
        _logger = logger;
    }

    public Task NotifyInfoAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("通知(INFO): {Title} - {Message}", title, message);
        return Task.CompletedTask;
    }

    public Task NotifyErrorAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        _logger.LogError("通知(ERROR): {Title} - {Message}", title, message);
        return Task.CompletedTask;
    }
}
