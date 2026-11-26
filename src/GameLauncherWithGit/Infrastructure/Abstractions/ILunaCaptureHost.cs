using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface ILunaCaptureHost
{
    event Action<CaptureSource, string>? Output;
    event Action<uint, bool>? ConnectionChanged;
    /// <summary>Waits for callbacks from a previous connection to the PID before a new subscriber is installed.</summary>
    Task WaitForPreviousConnectionAsync(int processId) => Task.CompletedTask;
    Task ConnectAsync(int processId);
    void Detach(int processId);
    /// <summary>Detaches and reports whether the previous connection can no longer produce callbacks.</summary>
    Task<bool> DetachAndDrainAsync(int processId)
    {
        Detach(processId);
        return Task.FromResult(false);
    }
    IReadOnlyList<CaptureProcess> GetProcesses();
    CaptureProcess? GetForegroundProcess();
}
