using GameLauncherWithGit.Domain.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class TrayService : ITrayService
{
	private readonly ILogger<TrayService> _logger;

	public TrayService(ILogger<TrayService> logger)
	{
		_logger = logger;
	}

	public void SetState(RepositorySyncState state)
	{
		_logger.LogInformation("Tray placeholder state changed. state={State}", state);
	}
}
