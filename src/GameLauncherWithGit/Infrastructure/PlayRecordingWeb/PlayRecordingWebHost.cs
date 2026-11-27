using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

/// <summary>The result of starting the desktop application's recording Web listener.</summary>
internal sealed record PlayRecordingWebStartup(Uri? Address, string? Failure = null);

/// <summary>Owns the loopback listener and closes it with the desktop application.</summary>
public sealed class PlayRecordingWebHost(PlayRecordingWebContent? content = null) : IAsyncDisposable
{
    public const int LoopbackPort = 47832;
    private readonly SemaphoreSlim _listenerLifecycle = new(1);
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource<PlayRecordingWebStartup> _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _disposeLock = new();
    private WebApplication? _app;
    private Uri? _localOrigin;
    private Task? _disposeTask;

    /// <summary>Completes when the scheduled startup succeeds, fails, or is stopped.</summary>
    internal Task<PlayRecordingWebStartup> Startup => _startup.Task;

    /// <summary>Starts the loopback listener on the fixed product port.</summary>
    public Task<Uri> StartLocalAsync() => StartLocalCoreAsync(LoopbackPort);

    /// <summary>Starts the same listener on an OS-assigned port for an isolated desktop or fixture.</summary>
    internal Task<Uri> StartIsolatedAsync() => StartLocalCoreAsync(0);

    /// <summary>Stops admission and closes the listener for the rest of the desktop lifetime.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new(_disposeTask ??= DisposeCoreAsync());
    }

    /// <summary>Closes admission before waiting for any overlapping startup.</summary>
    private async Task DisposeCoreAsync()
    {
        try { await _closing.CancelAsync(); }
        finally
        {
            // Settings can already be waiting for startup when exit interrupts it.
            _startup.TrySetResult(new(null));
            await StopListenerAsync();
        }
    }

    /// <summary>Releases the listener even if stopping it fails.</summary>
    private async Task StopListenerAsync()
    {
        await _listenerLifecycle.WaitAsync();
        try
        {
            _closing.Dispose();
            var app = _app;
            _app = null;
            _localOrigin = null;
            // The host might have been closed before it could bind.
            if (app is null) return;
            try { await app.StopAsync(); }
            finally { await app.DisposeAsync(); }
        }
        finally { _listenerLifecycle.Release(); }
    }

    /// <summary>Publishes the address only after Kestrel binds successfully.</summary>
    internal async Task<Uri> StartLocalCoreAsync(int port)
    {
        await _listenerLifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_closing.IsCancellationRequested, this);
            // Repeated startup requests share the listener, not a second bind.
            if (_localOrigin is { } running) return running;
            var app = PlayRecordingWebApplication.Build(port, _closing.Token, content);
            try
            {
                await app.StartAsync(_closing.Token);
                _closing.Token.ThrowIfCancellationRequested();
                var boundPort = port == 0
                    ? new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port
                    : port;
                _localOrigin = new UriBuilder(Uri.UriSchemeHttp, "localhost", boundPort).Uri;
                _app = app;
                _startup.TrySetResult(new(_localOrigin));
                return _localOrigin;
            }
            catch
            {
                // Startup did not take ownership of this application.
                await app.DisposeAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            // Surface only known recovery information, not raw listener or process details.
            if (!_closing.IsCancellationRequested)
                _startup.TrySetResult(new(null, ex is IOException { InnerException: AddressInUseException } or AddressInUseException
                    ? $"ポート {port} は使用中です。使用しているアプリを閉じてから再起動してください。"
                    : "記録Webを起動できませんでした。PCアプリを再起動してください。"));
            throw;
        }
        finally { _listenerLifecycle.Release(); }
    }
}
