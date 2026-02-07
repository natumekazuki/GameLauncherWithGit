using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.Collections.Concurrent;
#endif

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class NotificationService : INotificationService
{
	private readonly IAppSettingsService _appSettingsService;
	private readonly ILogger<NotificationService> _logger;
#if WINDOWS
	private readonly ConcurrentDictionary<string, DateTimeOffset> _recentNotifications = new(StringComparer.Ordinal);
	private bool _isRegistered;
#endif

	public NotificationService(
		IAppSettingsService appSettingsService,
		ILogger<NotificationService> logger)
	{
		_appSettingsService = appSettingsService;
		_logger = logger;

#if WINDOWS
		TryRegisterNotificationManager();
#endif
	}

	public Task NotifyAsync(
		string title,
		string message,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var normalizedTitle = NormalizeText(title, fallback: "GameLauncherWithGit");
		var normalizedMessage = NormalizeText(message, fallback: "(メッセージなし)");

#if WINDOWS
		if (IsSuppressed(normalizedTitle, normalizedMessage))
		{
			_logger.LogDebug(
				"Notification suppressed. title={Title}, message={Message}",
				normalizedTitle,
				normalizedMessage);
			return Task.CompletedTask;
		}

		try
		{
			var builder = new AppNotificationBuilder()
				.AddText(normalizedTitle)
				.AddText(normalizedMessage);
			var notification = builder.BuildNotification();
			AppNotificationManager.Default.Show(notification);

			_logger.LogInformation(
				"Windows notification sent. title={Title}, message={Message}",
				normalizedTitle,
				normalizedMessage);
			return Task.CompletedTask;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(
				ex,
				"Windows notification failed. fallback to log only. title={Title}, message={Message}",
				normalizedTitle,
				normalizedMessage);
		}
#endif

		_logger.LogInformation("Notification fallback. title={Title}, message={Message}", normalizedTitle, normalizedMessage);
		return Task.CompletedTask;
	}

	private static string NormalizeText(string? value, string fallback)
	{
		var normalized = value?.Trim();
		return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
	}

#if WINDOWS
	private void TryRegisterNotificationManager()
	{
		try
		{
			AppNotificationManager.Default.Register();
			_isRegistered = true;
		}
		catch (Exception ex)
		{
			_isRegistered = false;
			_logger.LogWarning(ex, "Windows notification manager registration failed.");
		}
	}

	private bool IsSuppressed(string title, string message)
	{
		if (!_isRegistered)
		{
			return false;
		}

		var key = $"{title}\n{message}";
		var suppressSeconds = Math.Max(0, _appSettingsService.Get().NotificationSuppressSeconds);
		if (suppressSeconds == 0)
		{
			return false;
		}

		var suppressWindow = TimeSpan.FromSeconds(suppressSeconds);
		var now = DateTimeOffset.Now;
		if (_recentNotifications.TryGetValue(key, out var previous) && now - previous < suppressWindow)
		{
			return true;
		}

		_recentNotifications[key] = now;
		return false;
	}
#endif
}
