namespace GameLauncherWithGit.Application.Models;

public sealed record CaptureWindow(nint Handle, int ProcessId, string ProcessName, string Title)
{
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? $"タイトルなし (0x{Handle:X})" : Title;
}

public sealed record ScenarioScreenshotFrame(int Width, int Height, byte[] PngBytes, string PixelHash);

public sealed record ScreenshotHotkey(uint Modifiers, uint VirtualKey);
