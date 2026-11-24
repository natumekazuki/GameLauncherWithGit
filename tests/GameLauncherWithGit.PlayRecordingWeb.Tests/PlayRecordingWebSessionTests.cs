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
