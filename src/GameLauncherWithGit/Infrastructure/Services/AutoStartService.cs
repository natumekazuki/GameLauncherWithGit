using GameLauncherWithGit.Infrastructure.Abstractions;
#if WINDOWS
using Microsoft.Win32;
#endif

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class AutoStartService : IAutoStartService
{
#if WINDOWS
	private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "GameLauncherWithGit";
#else
	private bool _enabled;
#endif

	public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
	{
#if WINDOWS
		cancellationToken.ThrowIfCancellationRequested();
		using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
		if (runKey is null)
		{
			return Task.FromResult(false);
		}

		var rawValue = runKey.GetValue(ValueName) as string;
		var isEnabled = !string.IsNullOrWhiteSpace(rawValue);
		return Task.FromResult(isEnabled);
#else
		return Task.FromResult(_enabled);
#endif
	}

	public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
	{
#if WINDOWS
		cancellationToken.ThrowIfCancellationRequested();
		using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
			?? throw new InvalidOperationException("自動起動設定のレジストリキーを開けません。");

		if (enabled)
		{
			runKey.SetValue(ValueName, BuildStartupCommand(), RegistryValueKind.String);
		}
		else
		{
			runKey.DeleteValue(ValueName, throwOnMissingValue: false);
		}
#else
		_enabled = enabled;
#endif
		return Task.CompletedTask;
	}

#if WINDOWS
	private static string BuildStartupCommand()
	{
		var processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath))
		{
			processPath = Path.Combine(AppContext.BaseDirectory, "GameLauncherWithGit.exe");
		}

		return $"\"{processPath}\"";
	}
#endif
}
