using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

/// <summary>Authorizes recording operations in the admission mode fixed for this circuit.</summary>
public sealed class PlayRecordingWebSession
{
    private readonly PlayRecordingWebAccess _access;
    private string? _epoch;
    private Uri? _localOrigin;
    // Odd versions have a live connection; every new connection gets its own version.
    private long _connectionVersion;

    internal PlayRecordingWebSession(PlayRecordingWebAccess access) => _access = access;

    /// <summary>Whether this circuit was admitted directly on a local browser origin.</summary>
    public bool FollowsDesktopTarget => _localOrigin is not null;

    /// <summary>
    /// Executes the entire read or mutation under the current grant, passing the server-selected game ID.
    /// The callback must await the operation and must not recursively enter this session.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The circuit is local, disconnected, or its grant is no longer current.</exception>
    public Task<T> UseAsync<T>(Func<string, Task<T>> operation) => UseAsync(ConnectionVersion, operation);

    internal Task<T> UseAsync<T>(long connectionVersion, Func<string, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (FollowsDesktopTarget) throw new UnauthorizedAccessException();
        return _access.UseAsync(_epoch, () => IsConnectionCurrent(connectionVersion), operation);
    }

    /// <summary>
    /// Executes an operation on the current local connection without receiving or resolving a game ID.
    /// The operation must enforce its own desktop-target lifetime and await its work.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The circuit is not local or its connection is no longer current.</exception>
    public Task<T> UseDesktopTargetAsync<T>(Func<Task<T>> operation) => UseDesktopTargetAsync(ConnectionVersion, operation);

    internal async Task<T> UseDesktopTargetAsync<T>(long connectionVersion, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!FollowsDesktopTarget || !IsConnectionCurrent(connectionVersion)) throw new UnauthorizedAccessException();
        return await operation();
    }

    internal long ConnectionVersion => Interlocked.Read(ref _connectionVersion);
    internal bool IsConnectionCurrent(long version) => !_access.IsClosed && (version & 1) != 0 && ConnectionVersion == version;

    internal void Open(string? epoch)
    {
        if (!_access.IsCurrent(epoch)) throw new UnauthorizedAccessException();
        _epoch = epoch;
    }

    internal void OpenLocal(Uri origin) => _localOrigin = origin;

    internal bool MatchesConnection(PlayRecordingWebRequest request, string? epoch) =>
        request.IsLocal ? _localOrigin == request.Origin : !FollowsDesktopTarget && _epoch == epoch;

    internal void Connect()
    {
        if (_access.IsClosed || (!FollowsDesktopTarget && !_access.IsCurrent(_epoch))) throw new UnauthorizedAccessException();
        SetConnected(true);
    }

    internal void Disconnect() => SetConnected(false);

    private void SetConnected(bool connected)
    {
        long previous;
        do
        {
            previous = ConnectionVersion;
            if (((previous & 1) != 0) == connected) return;
        } while (Interlocked.CompareExchange(ref _connectionVersion, previous + 1, previous) != previous);
    }
}

internal sealed class PlayRecordingWebCircuitHandler(
    PlayRecordingWebSession session,
    AuthenticationStateProvider authentication,
    IHttpContextAccessor transport) : CircuitHandler
{
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        var request = transport.HttpContext?.Features.Get<PlayRecordingWebRequest>()
            ?? throw new UnauthorizedAccessException();
        if (request.IsLocal) session.OpenLocal(request.Origin);
        else session.Open(PlayRecordingWebAccess.ReadEpoch(user));
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // The framework updates AuthenticationStateProvider after this callback on reconnect.
        // Check the new transport here instead; the circuit keeps its original admission.
        var context = transport.HttpContext;
        if (context?.Features.Get<PlayRecordingWebRequest>() is not { } request
            || !session.MatchesConnection(request, PlayRecordingWebAccess.ReadEpoch(context.User)))
            throw new UnauthorizedAccessException();
        session.Connect();
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        session.Disconnect();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        session.Disconnect();
        return Task.CompletedTask;
    }
}
