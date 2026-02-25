using GameLauncherWithGit.Infrastructure.Abstractions;
#if WINDOWS
using Microsoft.Win32;
using Windows.ApplicationModel;
#endif

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class AutoStartService : IAutoStartService
{
#if WINDOWS
	private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string StartupApprovedRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
	private const string StartupApprovedRun32KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
	private const string ApplicationFileName = "GameLauncherWithGit";
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
		var isEnabled = !string.IsNullOrWhiteSpace(rawValue) && !IsStartupDisabledByWindows();
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
			ClearStartupApprovedState();
		}
		else
		{
			runKey.DeleteValue(ValueName, throwOnMissingValue: false);
			ClearStartupApprovedState();
		}
#else
		_enabled = enabled;
#endif
		return Task.CompletedTask;
	}

#if WINDOWS
	private static string BuildStartupCommand()
	{
		var packagedStartupCommand = TryBuildPackagedStartupCommand();
		if (!string.IsNullOrWhiteSpace(packagedStartupCommand))
		{
			return packagedStartupCommand;
		}

		var processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath))
		{
			processPath = Path.Combine(AppContext.BaseDirectory, $"{ApplicationFileName}.exe");
		}

		var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{ApplicationFileName}.dll");
		if (IsDotNetHostPath(processPath) && File.Exists(assemblyPath))
		{
			return $"\"{processPath}\" \"{assemblyPath}\"";
		}

		return $"\"{processPath}\"";
	}

	private static string? TryBuildPackagedStartupCommand()
	{
		try
		{
			var packageFamilyName = Package.Current.Id.FamilyName;
			if (string.IsNullOrWhiteSpace(packageFamilyName))
			{
				return null;
			}

			return $"explorer.exe shell:AppsFolder\\{packageFamilyName}!App";
		}
		catch
		{
			return null;
		}
	}

	private static bool IsDotNetHostPath(string processPath)
	{
		var fileName = Path.GetFileName(processPath);
		return fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
			|| fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsStartupDisabledByWindows()
	{
		return IsStartupDisabledByWindows(StartupApprovedRunKeyPath)
			|| IsStartupDisabledByWindows(StartupApprovedRun32KeyPath);
	}

	private static bool IsStartupDisabledByWindows(string keyPath)
	{
		using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
		var rawValue = key?.GetValue(ValueName) as byte[];
		if (rawValue is not { Length: > 0 })
		{
			return false;
		}

		var state = rawValue[0];
		return state is 0x03 or 0x07;
	}

	private static void ClearStartupApprovedState()
	{
		ClearStartupApprovedState(StartupApprovedRunKeyPath);
		ClearStartupApprovedState(StartupApprovedRun32KeyPath);
	}

	private static void ClearStartupApprovedState(string keyPath)
	{
		using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
		key?.DeleteValue(ValueName, throwOnMissingValue: false);
	}
#endif
}
