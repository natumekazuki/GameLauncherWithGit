using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class NotificationService : INotificationService
{
	private readonly ILogger<NotificationService> _logger;

	public NotificationService(ILogger<NotificationService> logger)
	{
		_logger = logger;
	}

	public Task NotifyAsync(
		string title,
		string message,
		CancellationToken cancellationToken = default)
	{
		_logger.LogInformation("Notification placeholder. title={Title}, message={Message}", title, message);
		return Task.CompletedTask;
	}
}
