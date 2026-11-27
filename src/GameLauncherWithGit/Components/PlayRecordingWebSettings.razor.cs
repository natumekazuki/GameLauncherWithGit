using Microsoft.AspNetCore.Components;
#if WINDOWS
using GameLauncherWithGit.Infrastructure.PlayRecordingWeb;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
#endif

namespace GameLauncherWithGit.Components;

/// <summary>Shows the desktop listener's startup result and provides its local URL actions.</summary>
public partial class PlayRecordingWebSettings
{
    private Uri? _address;
    private string _hostStatus = "起動中";
    private string? _startupFailure;
    private string? _actionStatus;
    private string _copyLabel = "コピー";
    private string _openLabel = "開く";
    private bool _busy;

#if WINDOWS
    [Inject] private PlayRecordingWebHost Host { get; set; } = default!;
    [Inject] private IClipboard Clipboard { get; set; } = default!;
    [Inject] private ILauncher Launcher { get; set; } = default!;

    /// <summary>Observes automatic startup without starting or restarting the listener from settings.</summary>
    protected override async Task OnInitializedAsync()
    {
        var startup = await Host.Startup;
        _address = startup.Address;
        _startupFailure = startup.Failure;
        _hostStatus = startup.Failure is not null ? "起動失敗" : startup.Address is not null ? "公開中" : "停止";
    }
#endif

    /// <summary>Copies the exact published address using the desktop clipboard.</summary>
    private Task CopyAsync() =>
#if WINDOWS
        RunUrlActionAsync(async address =>
        {
            await Clipboard.SetTextAsync(address.AbsoluteUri);
            return true;
        }, "URLをコピーしました。", "URLをコピーできませんでした。",
            ok => _copyLabel = ok ? "コピー済み" : "コピー失敗");
#else
        Task.CompletedTask;
#endif

    /// <summary>Opens the published address in the desktop's default handler.</summary>
    private Task OpenAsync() =>
#if WINDOWS
        RunUrlActionAsync(Launcher.OpenAsync, null, "記録Webを開けませんでした。",
            ok => _openLabel = ok ? "開く" : "開くのに失敗");
#else
        Task.CompletedTask;
#endif

    /// <summary>Reports the action result in place without moving either action.</summary>
    private async Task RunUrlActionAsync(Func<Uri, Task<bool>> operation, string? success, string failure, Action<bool> apply)
    {
        if (_busy || _address is not { } address) return;
        _busy = true;
        _actionStatus = null;
        bool ok;
        try { ok = await operation(address); }
        catch (Exception) { ok = false; } // Platform APIs can fail without returning an action result.
        finally { _busy = false; }
        apply(ok);
        _actionStatus = ok ? success : failure;
    }
}
