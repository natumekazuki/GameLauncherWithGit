using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

/// <summary>Authorizes record operations for the game fixed by the PC grant of this circuit.</summary>
public sealed class PlayRecordingWebSession
{
    private readonly PlayRecordingWebAccess _access;
    private string? _epoch;
    // Odd versions have a live connection; every new connection gets its own version.
    private long _connectionVersion;

    internal PlayRecordingWebSession(PlayRecordingWebAccess access) => _access = access;

    /// <summary>
    /// Executes the entire read or mutation under the current grant, passing the server-selected game ID.
    /// The callback must await the operation and must not recursively enter this session.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The circuit is disconnected or its grant is no longer current.</exception>
    public Task<T> UseAsync<T>(Func<string, Task<T>> operation) => UseAsync(ConnectionVersion, operation);

    internal Task<T> UseAsync<T>(long connectionVersion, Func<string, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _access.UseAsync(_epoch, () => IsConnectionCurrent(connectionVersion), operation);
    }

    internal long ConnectionVersion => Interlocked.Read(ref _connectionVersion);
    internal bool IsConnectionCurrent(long version) => (version & 1) != 0 && ConnectionVersion == version;

    internal void Open(string? epoch)
    {
        if (!_access.IsCurrent(epoch)) throw new UnauthorizedAccessException();
        _epoch = epoch;
    }

    internal void Connect()
    {
        if (!_access.IsCurrent(_epoch)) throw new UnauthorizedAccessException();
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
    AuthenticationStateProvider authentication) : CircuitHandler
{
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        session.Open(PlayRecordingWebAccess.ReadEpoch(user));
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // The framework updates AuthenticationStateProvider after reconnecting. Never rebind
        // a resumed circuit to the new cookie: the circuit belongs to its original epoch.
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
