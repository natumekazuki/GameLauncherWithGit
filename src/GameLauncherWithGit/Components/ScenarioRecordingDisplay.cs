namespace GameLauncherWithGit.Components;

/// <summary>Presentation state for the current recording target; not an authorization or a service state.</summary>
public enum ScenarioRecordingDisplay
{
    NotRecording,
    Unconfigured,
    Preparing,
    WaitingForText,
    Recording,
    Paused,
    WaitingAfterResume,
    Failed,
}
