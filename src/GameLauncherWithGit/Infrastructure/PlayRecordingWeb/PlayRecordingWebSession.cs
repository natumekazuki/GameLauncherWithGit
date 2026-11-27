using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

/// <summary>Tracks the browser origin and connection generation of a recording circuit.</summary>
internal sealed class PlayRecordingWebSession(CancellationToken hostClosing)
{
    private Uri? _origin;
    // Odd versions have a live connection; every new connection gets its own version.
    internal long ConnectionVersion => Interlocked.Read(ref _connectionVersion);
    private long _connectionVersion;

    /// <summary>Rejects work from an earlier connection even if the same circuit is now live.</summary>
    internal bool IsConnectionCurrent(long version) =>
        !hostClosing.IsCancellationRequested && (version & 1) != 0 && ConnectionVersion == version;

    /// <summary>Binds a new circuit to the transport's validated browser origin.</summary>
    internal void Open(Uri origin) => _origin = origin;

    /// <summary>Allows reconnect only through the circuit's original browser origin.</summary>
    internal void Connect(Uri origin)
    {
        if (hostClosing.IsCancellationRequested || _origin != origin)
            throw new InvalidOperationException("The recording circuit has no matching live transport.");
        SetConnected(true);
    }

    /// <summary>Invalidates any work carrying the disconnected generation.</summary>
    internal void Disconnect() => SetConnected(false);

    /// <summary>Advances the generation once for each transport transition.</summary>
    private void SetConnected(bool connected)
    {
        long previous;
        // Circuit close and connection down can both report the same disconnect.
        do
        {
            previous = ConnectionVersion;
            if (((previous & 1) != 0) == connected) return;
        } while (Interlocked.CompareExchange(ref _connectionVersion, previous + 1, previous) != previous);
    }
}

/// <summary>Converts Blazor transport events into circuit connection transitions.</summary>
internal sealed class PlayRecordingWebCircuitHandler(
    PlayRecordingWebSession session,
    IHttpContextAccessor transport) : CircuitHandler
{
    /// <summary>Reads the origin validated for the initial transport.</summary>
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        session.Open(GetTransportOrigin());
        return Task.CompletedTask;
    }

    /// <summary>Checks the current transport on start and on reconnect.</summary>
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        session.Connect(GetTransportOrigin());
        return Task.CompletedTask;
    }

    /// <summary>Immediately invalidates the old generation on disconnect.</summary>
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        session.Disconnect();
        return Task.CompletedTask;
    }

    /// <summary>Prevents work after disposal of the Blazor circuit.</summary>
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        session.Disconnect();
        return Task.CompletedTask;
    }

    /// <summary>Gets only the browser origin checked by admission middleware.</summary>
    private Uri GetTransportOrigin() => transport.HttpContext?.Features.Get<PlayRecordingWebRequest>()?.Origin
        ?? throw new InvalidOperationException("The recording circuit has no admitted transport.");
}
