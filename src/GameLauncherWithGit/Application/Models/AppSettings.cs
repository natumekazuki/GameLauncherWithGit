namespace GameLauncherWithGit.Application.Models;

public sealed record AppSettings(
	int SyncDebounceSeconds,
	int SyncRetryInitialSeconds,
	int SyncRetryMaxSeconds,
	int NotificationSuppressSeconds,
	int LogRetentionDays,
	int LogMaxFileSizeMb,
	int GameCardSizePercent = 100,
	bool ShowCardTitle = true,
	bool ShowCardSyncStatus = true,
	int WindowWidth = AppSettings.DefaultWindowWidth,
	int WindowHeight = AppSettings.DefaultWindowHeight)
{
	public const int GameCardSizePercentMin = 10;
	public const int GameCardSizePercentMax = 500;
	public const int DefaultWindowWidth = 1280;
	public const int DefaultWindowHeight = 820;
	public const int WindowWidthMin = 900;
	public const int WindowWidthMax = 3840;
	public const int WindowHeightMin = 600;
	public const int WindowHeightMax = 2160;

	public static AppSettings Default { get; } = new(
		SyncDebounceSeconds: 10,
		SyncRetryInitialSeconds: 5,
		SyncRetryMaxSeconds: 300,
		NotificationSuppressSeconds: 20,
		LogRetentionDays: 30,
		LogMaxFileSizeMb: 20,
		GameCardSizePercent: 100,
		ShowCardTitle: true,
		ShowCardSyncStatus: true,
		WindowWidth: DefaultWindowWidth,
		WindowHeight: DefaultWindowHeight);

	public AppSettings Normalize()
	{
		var debounce = Clamp(SyncDebounceSeconds, min: 1, max: 300);
		var retryInitial = Clamp(SyncRetryInitialSeconds, min: 1, max: 600);
		var retryMax = Clamp(SyncRetryMaxSeconds, min: retryInitial, max: 3600);
		var suppress = Clamp(NotificationSuppressSeconds, min: 0, max: 600);
		var retentionDays = LogRetentionDays <= 0
			? Default.LogRetentionDays
			: Clamp(LogRetentionDays, min: 1, max: 365);
		var maxFileSizeMb = LogMaxFileSizeMb <= 0
			? Default.LogMaxFileSizeMb
			: Clamp(LogMaxFileSizeMb, min: 1, max: 1024);
		var gameCardSizePercent = GameCardSizePercent <= 0
			? Default.GameCardSizePercent
			: Clamp(GameCardSizePercent, min: GameCardSizePercentMin, max: GameCardSizePercentMax);
		var showCardTitle = ShowCardTitle;
		var showCardSyncStatus = ShowCardSyncStatus;
		var windowWidth = WindowWidth <= 0
			? Default.WindowWidth
			: Clamp(WindowWidth, min: WindowWidthMin, max: WindowWidthMax);
		var windowHeight = WindowHeight <= 0
			? Default.WindowHeight
			: Clamp(WindowHeight, min: WindowHeightMin, max: WindowHeightMax);

		return this with
		{
			SyncDebounceSeconds = debounce,
			SyncRetryInitialSeconds = retryInitial,
			SyncRetryMaxSeconds = retryMax,
			NotificationSuppressSeconds = suppress,
			LogRetentionDays = retentionDays,
			LogMaxFileSizeMb = maxFileSizeMb,
			GameCardSizePercent = gameCardSizePercent,
			ShowCardTitle = showCardTitle,
			ShowCardSyncStatus = showCardSyncStatus,
			WindowWidth = windowWidth,
			WindowHeight = windowHeight
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
