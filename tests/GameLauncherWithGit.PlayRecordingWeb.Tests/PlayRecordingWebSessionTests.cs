using GameLauncherWithGit.Infrastructure.PlayRecordingWeb;
using Xunit;

namespace GameLauncherWithGit.PlayRecordingWeb.Tests;

/// <summary>Verifies that connection lifetime does not depend on a user or game grant.</summary>
public sealed class PlayRecordingWebSessionTests
{
    /// <summary>V03: disconnected and superseded generations cannot pass the gate used by displayed recording operations.</summary>
    [Fact]
    public void ReconnectRejectsTheDisconnectedGeneration()
    {
        using var closing = new CancellationTokenSource();
        var circuit = new PlayRecordingWebSession(closing.Token);
        var origin = new Uri("http://localhost:47832/");
        circuit.Open(origin);
        Assert.False(circuit.IsConnectionCurrent(circuit.ConnectionVersion));
        circuit.Connect(origin);
        var before = circuit.ConnectionVersion;
        Assert.True(circuit.IsConnectionCurrent(before));

        // Closing twice is equivalent to one transport transition.
        circuit.Disconnect();
        var disconnected = circuit.ConnectionVersion;
        circuit.Disconnect();
        Assert.Equal(disconnected, circuit.ConnectionVersion);
        Assert.False(circuit.IsConnectionCurrent(before));
        Assert.False(circuit.IsConnectionCurrent(disconnected));

        // Changing the browser origin is not a reconnect to the same circuit.
        Assert.Throws<InvalidOperationException>(() => circuit.Connect(new Uri("http://127.0.0.1:47832/")));
        Assert.False(circuit.IsConnectionCurrent(circuit.ConnectionVersion));
        circuit.Connect(origin);
        Assert.False(circuit.IsConnectionCurrent(before));
        Assert.True(circuit.IsConnectionCurrent(circuit.ConnectionVersion));

        closing.Cancel();
        Assert.False(circuit.IsConnectionCurrent(circuit.ConnectionVersion));
        Assert.Throws<InvalidOperationException>(() => circuit.Connect(origin));
    }
}
