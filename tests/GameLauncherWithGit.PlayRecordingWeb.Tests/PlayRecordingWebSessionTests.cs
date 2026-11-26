using GameLauncherWithGit.Infrastructure.PlayRecordingWeb;
using Xunit;

namespace GameLauncherWithGit.PlayRecordingWeb.Tests;

public sealed class PlayRecordingWebSessionTests
{
    // @test-value v2
    // kind = "invariant"
    // claim = "An authorized operation completes against the original game before revocation can commit. A replacement grant cannot rebind the old circuit."
    // oracle = { type = "contract", ref = "src/GameLauncherWithGit/Infrastructure/PlayRecordingWeb/PlayRecordingWebSession.cs: UseAsync<T> API contract; src/GameLauncherWithGit/Infrastructure/PlayRecordingWeb/PlayRecordingWebHost.cs: RevokeAsync and IssueAsync API contracts" }
    // fault = "Revocation proceeds while an authorized operation is waiting, or a previously authorized circuit reconnects and executes against the replacement game."
    // observable = "Revocation remains pending while the callback waits; the callback returns the original game; reconnect and further reads throw; a new circuit receives the new game."
    // observation_boundary = "component-behavior"
    // scope = "recording-web-game-grant"
    // lifecycle = "permanent"
    // risk_tags = ["authorization"]
    // @end-test-value
    [Fact]
    public async Task RevocationWaitsForAuthorizedOperationAndCannotRebindOldCircuit()
    {
        var access = new PlayRecordingWebAccess(TimeProvider.System);
        var oldCircuit = await OpenCircuitAsync(access, "game-one");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = oldCircuit.UseAsync(async gameId =>
        {
            entered.SetResult();
            await complete.Task;
            return gameId;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var revocation = access.RevokeAsync();
        try { Assert.False(revocation.IsCompleted); }
        finally { complete.TrySetResult(); }

        Assert.Equal("game-one", await inFlight);
        await revocation;
        var newCircuit = await OpenCircuitAsync(access, "game-two");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => oldCircuit.UseAsync<string>(_ => throw new InvalidOperationException("A stale callback must not run.")));
        oldCircuit.Disconnect();
        Assert.Throws<UnauthorizedAccessException>(oldCircuit.Connect);
        Assert.Equal("game-two", await newCircuit.UseAsync<string>(Task.FromResult));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "A disconnected circuit cannot execute a recording read even when the grant remains current."
    // oracle = { type = "contract", ref = "src/GameLauncherWithGit/Infrastructure/PlayRecordingWeb/PlayRecordingWebSession.cs: UseAsync<T> API contract and disconnected exception" }
    // fault = "Operations rely on the grant epoch alone and can run while the originating circuit has no connection."
    // observable = "Read callback is not entered and UseAsync throws while disconnected; reconnection to the unchanged grant returns the same game."
    // observation_boundary = "component-behavior"
    // scope = "recording-web-circuit"
    // lifecycle = "permanent"
    // risk_tags = ["authorization"]
    // @end-test-value
    [Fact]
    public async Task DisconnectedCircuitCannotReadBeforeReconnectingToItsGrant()
    {
        var access = new PlayRecordingWebAccess(TimeProvider.System);
        var circuit = await OpenCircuitAsync(access, "allowed-game");
        circuit.Disconnect();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => circuit.UseAsync<string>(_ => throw new InvalidOperationException("A disconnected callback must not run.")));
        circuit.Connect();
        Assert.Equal("allowed-game", await circuit.UseAsync<string>(Task.FromResult));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "Reconnection does not carry over a queued operation that had not been admitted before the disconnect."
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / play-web-product-coordination (no replay after disconnect)" }
    // fault = "The authorization gate checks only whether the circuit is currently connected and admits an operation queued by a previous connection."
    // observable = "An already admitted callback completes; a queued callback is never entered; a fresh callback on the resumed connection can read the original game."
    // observation_boundary = "component-behavior"
    // scope = "recording-web-queued-operation-lifetime"
    // lifecycle = "permanent"
    // risk_tags = ["authorization"]
    // @end-test-value
    [Fact]
    public async Task ReconnectingDoesNotKeepOperationsQueuedByThePreviousConnection()
    {
        var access = new PlayRecordingWebAccess(TimeProvider.System);
        var circuit = await OpenCircuitAsync(access, "allowed-game");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = circuit.UseAsync(async gameId =>
        {
            entered.SetResult();
            await release.Task;
            return gameId;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = circuit.UseAsync<string>(_ => throw new InvalidOperationException("An old queued callback must not run."));
        try
        {
            circuit.Disconnect();
            circuit.Connect();
        }
        finally { release.TrySetResult(); }

        Assert.Equal("allowed-game", await inFlight);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queued);
        Assert.Equal("allowed-game", await circuit.UseAsync<string>(Task.FromResult));
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "A local circuit uses only the desktop-target operation; a Serve circuit uses only the granted game operation. Disconnected or superseded local connections, or connections on a closing host, cannot operate."
    // oracle = { type = "contract", ref = "src/GameLauncherWithGit/Infrastructure/PlayRecordingWeb/PlayRecordingWebSession.cs: FollowsDesktopTarget, UseDesktopTargetAsync<T> and UseAsync<T>" }
    // fault = "Either circuit accepts the other mode's callback, or a local circuit can act while disconnected, from a prior connection or after the host closes admission."
    // observable = "Mode flags, current callback results, and UnauthorizedAccessException without invoking a callback in the wrong mode or on a disconnected, stale, or closing connection."
    // observation_boundary = "component-behavior"
    // scope = "recording-web-circuit-admission"
    // lifecycle = "permanent"
    // risk_tags = ["authorization"]
    // @end-test-value
    [Fact]
    public async Task LocalAndServeCircuitsCannotUseEachOthersOperation()
    {
        var access = new PlayRecordingWebAccess(TimeProvider.System);
        var serve = await OpenCircuitAsync(access, "allowed-game");
        var local = new PlayRecordingWebSession(access);
        local.OpenLocal(new Uri("http://localhost:47832/"));
        local.Connect();

        Assert.True(local.FollowsDesktopTarget);
        Assert.False(serve.FollowsDesktopTarget);
        Assert.Equal("allowed-game", await serve.UseAsync<string>(Task.FromResult));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            serve.UseDesktopTargetAsync<int>(() => throw new InvalidOperationException("Serve must not use the desktop target.")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            local.UseAsync<int>(_ => throw new InvalidOperationException("Local must not receive the Serve game.")));

        await access.RevokeAsync();
        Assert.Equal(7, await local.UseDesktopTargetAsync(() => Task.FromResult(7)));
        var previous = local.ConnectionVersion;
        local.Disconnect();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            local.UseDesktopTargetAsync<int>(() => throw new InvalidOperationException("A disconnected circuit must not operate.")));
        local.Connect();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            local.UseDesktopTargetAsync<int>(previous, () => throw new InvalidOperationException("A stale connection must not operate.")));
        Assert.Equal(8, await local.UseDesktopTargetAsync(() => Task.FromResult(8)));
        access.Close();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            local.UseDesktopTargetAsync<int>(() => throw new InvalidOperationException("Desktop shutdown must close admission.")));
    }

    private static async Task<PlayRecordingWebSession> OpenCircuitAsync(PlayRecordingWebAccess access, string gameId)
    {
        await access.IssueAsync(gameId, gameId);
        var grant = await access.RedeemAsync(access.Permission!.Code!);
        var circuit = new PlayRecordingWebSession(access);
        circuit.Open(grant!.Value.Epoch);
        circuit.Connect();
        return circuit;
    }
}
