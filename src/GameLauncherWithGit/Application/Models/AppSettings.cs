namespace GameLauncherWithGit.Application.Models;

public sealed record AppSettings(
	int SyncDebounceSeconds,
	int SyncRetryInitialSeconds,
	int SyncRetryMaxSeconds,
	int NotificationSuppressSeconds)
{
	public static AppSettings Default { get; } = new(
		SyncDebounceSeconds: 10,
		SyncRetryInitialSeconds: 5,
		SyncRetryMaxSeconds: 300,
		NotificationSuppressSeconds: 20);

	public AppSettings Normalize()
	{
		var debounce = Clamp(SyncDebounceSeconds, min: 1, max: 300);
		var retryInitial = Clamp(SyncRetryInitialSeconds, min: 1, max: 600);
		var retryMax = Clamp(SyncRetryMaxSeconds, min: retryInitial, max: 3600);
		var suppress = Clamp(NotificationSuppressSeconds, min: 0, max: 600);

		return this with
		{
			SyncDebounceSeconds = debounce,
			SyncRetryInitialSeconds = retryInitial,
			SyncRetryMaxSeconds = retryMax,
			NotificationSuppressSeconds = suppress
		};
	}

	private static int Clamp(int value, int min, int max)
	{
		if (value < min)
		{
			return min;
		}

		if (value > max)
		{
			return max;
		}

		return value;
	}
}
