using GameLauncherWithGit.Application.Models;

namespace GameLauncherWithGit.Infrastructure.Abstractions;

public interface ILunaCaptureHost
{
    event Action<CaptureSource, string>? Output;
    event Action<uint, bool>? ConnectionChanged;
    Task ConnectAsync(int processId);
    void Detach(int processId);
    IReadOnlyList<CaptureProcess> GetProcesses();
    CaptureProcess? GetForegroundProcess();
}
