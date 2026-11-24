namespace GameLauncherWithGit.PlayRecordingWeb;

/// <summary>The root mount and server-selected component for the isolated Web UI.</summary>
public sealed record PlayRecordingWebView(Uri Origin, Type? PanelType, bool HasPanelStyle);
