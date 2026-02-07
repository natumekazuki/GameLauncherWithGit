using System.Text;
using System.Text.Json;
using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Application.Models;
using Microsoft.Extensions.Logging;

namespace GameLauncherWithGit.Application.Services;

public sealed class AppSettingsService : IAppSettingsService
{
	private const string SettingsFileName = "settings.json";
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};

	private readonly object _gate = new();
	private readonly ILogger<AppSettingsService> _logger;
	private readonly string _settingsFilePath;

	private AppSettings _current = AppSettings.Default;
	private bool _isLoaded;

	public AppSettingsService(ILogger<AppSettingsService> logger)
	{
		_logger = logger;
		_settingsFilePath = Path.Combine(FileSystem.AppDataDirectory, SettingsFileName);
	}

	public AppSettings Get()
	{
		lock (_gate)
		{
			EnsureLoadedCore();
			return _current;
		}
	}

	public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
	{
		var normalized = (settings ?? AppSettings.Default).Normalize();
		var payload = JsonSerializer.Serialize(normalized, JsonOptions);
		string path;

		lock (_gate)
		{
			EnsureDirectoryCore();
			path = _settingsFilePath;
		}

		await File.WriteAllTextAsync(path, payload, Encoding.UTF8, cancellationToken);

		lock (_gate)
		{
			_current = normalized;
			_isLoaded = true;
		}
	}

	private void EnsureLoadedCore()
	{
		if (_isLoaded)
		{
			return;
		}

		EnsureDirectoryCore();
		if (!File.Exists(_settingsFilePath))
		{
			_current = AppSettings.Default;
			_isLoaded = true;
			return;
		}

		try
		{
			var content = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
			var loaded = JsonSerializer.Deserialize<AppSettings>(content, JsonOptions);
			_current = (loaded ?? AppSettings.Default).Normalize();
		}
		catch (Exception ex)
		{
			_current = AppSettings.Default;
			_logger.LogWarning(ex, "Failed to load app settings. path={SettingsFilePath}", _settingsFilePath);
		}

		_isLoaded = true;
	}

	private void EnsureDirectoryCore()
	{
		var directory = Path.GetDirectoryName(_settingsFilePath);
		if (string.IsNullOrWhiteSpace(directory))
		{
			return;
		}

		Directory.CreateDirectory(directory);
	}
}
