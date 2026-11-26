using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

/// <summary>Owns the desktop lifetime of the loopback listener and the optional Serve grant.</summary>
public sealed class PlayRecordingWebHost(
    IGameLibraryStore games,
    IScenarioCaptureStore capture,
    PlayRecordingWebContent? content = null) : IAsyncDisposable
{
    public const int LoopbackPort = 47832;
    private readonly PlayRecordingWebAccess _access = new(TimeProvider.System);
    private readonly SemaphoreSlim _listenerLifecycle = new(1);
    private readonly SemaphoreSlim _serveChanges = new(1);
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly object _disposeLock = new();
    private WebApplication? _app;
    private Uri? _localOrigin;
    private Task? _disposeTask;

    public Uri? ServeOrigin => _access.ServeOrigin;
    public PlayRecordingWebPermission? Permission => _access.Permission;

    /// <summary>Lists registered games with a confirmed and enabled capture setup; never creates profiles.</summary>
    public async Task<IReadOnlyList<GameCardItem>> GetEligibleGamesAsync()
    {
        var configuredIds = (await capture.GetCaptureProfilesAsync())
            .Where(profile => profile.GameId is not null && profile.HasConfirmedRecordingSetup)
            .Select(profile => profile.GameId!).ToHashSet(StringComparer.Ordinal);
        return (await games.GetAllAsync()).Where(game => configuredIds.Contains(game.Id)).ToArray();
    }

    /// <summary>Starts the direct PC-only listener for the desktop lifetime on the fixed product port.</summary>
    public Task<Uri> StartLocalAsync() => StartLocalCoreAsync(LoopbackPort);

    /// <summary>Starts the same listener on an OS-assigned port for an isolated desktop or fixture.</summary>
    internal Task<Uri> StartIsolatedAsync() => StartLocalCoreAsync(0);

    /// <summary>Enables exactly this private HTTPS Serve origin and issues its first fixed-game grant.</summary>
    public async Task<PlayRecordingWebPermission> StartServeAsync(string gameId, Uri origin)
    {
        var serveOrigin = RequireServeOrigin(origin);
        await _serveChanges.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_access.IsClosed, this);
            if (Volatile.Read(ref _app) is null) throw new InvalidOperationException("記録Webの待受を利用できません。PCアプリを再起動してください。");
            if (ServeOrigin is not null) throw new InvalidOperationException("Serve接続はすでに開始されています。");
            var game = await RequireEligibleGameAsync(gameId);
            await _access.IssueAsync(game.Id, game.Title, serveOrigin);
            return Permission!;
        }
        finally { _serveChanges.Release(); }
    }

    /// <summary>Replaces the current grant and invalidates its cookie and circuits, without restarting the listener.</summary>
    public async Task<PlayRecordingWebPermission> IssueAsync(string gameId)
    {
        await _serveChanges.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_access.IsClosed, this);
            if (ServeOrigin is null) throw new InvalidOperationException("Serve接続が開始されていません。");
            var game = await RequireEligibleGameAsync(gameId);
            await _access.IssueAsync(game.Id, game.Title);
            return Permission!;
        }
        finally { _serveChanges.Release(); }
    }

    /// <summary>Invalidates access and waits for any operation already authorized under the old grant.</summary>
    public async Task RevokeAsync()
    {
        await _serveChanges.WaitAsync();
        try { await _access.RevokeAsync(); }
        finally { _serveChanges.Release(); }
    }

    /// <summary>Disables the Serve origin and revokes access. The local listener stays available.</summary>
    public async Task StopServeAsync()
    {
        await _serveChanges.WaitAsync();
        try { await _access.RevokeAsync(disableServe: true); }
        finally { _serveChanges.Release(); }
    }

    /// <summary>Stops admission and closes the listener for the rest of the desktop lifetime.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _access.Close();
        try { await _startupCancellation.CancelAsync(); }
        finally { await StopListenerAsync(); }
    }

    private async Task StopListenerAsync()
    {
        // Only startup owns the listener gate. Waiting on a Serve setting or grant here
        // would let a desktop read or a client operation stall application exit.
        await _listenerLifecycle.WaitAsync();
        try
        {
            _startupCancellation.Dispose();
            var app = Interlocked.Exchange(ref _app, null);
            _localOrigin = null;
            if (app is null) return;
            try { await app.StopAsync(); }
            finally { await app.DisposeAsync(); }
        }
        finally { _listenerLifecycle.Release(); }
    }

    private async Task<Uri> StartLocalCoreAsync(int port)
    {
        await _listenerLifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_access.IsClosed, this);
            if (_localOrigin is { } running) return running;
            var app = PlayRecordingWebApplication.Build(port, _access, content);
            try
            {
                await app.StartAsync(_startupCancellation.Token);
                var boundPort = port == 0
                    ? new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port
                    : port;
                _localOrigin = new UriBuilder(Uri.UriSchemeHttp, "127.0.0.1", boundPort).Uri;
                Volatile.Write(ref _app, app);
                return _localOrigin;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }
        finally { _listenerLifecycle.Release(); }
    }

    private async Task<GameCardItem> RequireEligibleGameAsync(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId) || await games.FindByIdAsync(gameId) is not { } game
            || await capture.FindCaptureProfileAsync(game.Id) is not { HasConfirmedRecordingSetup: true })
            throw new InvalidOperationException("登録済みで、シナリオ抽出の設定を確定したゲームを選んでください。");
        return game;
    }

    private static Uri RequireServeOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps || !origin.IsDefaultPort
            || origin.UserInfo.Length != 0 || origin.PathAndQuery != "/" || origin.Fragment.Length != 0
            || origin.HostNameType != UriHostNameType.Dns || !origin.IdnHost.EndsWith(".ts.net", StringComparison.Ordinal)
            || origin.IdnHost.Split('.').Length < 4)
            throw new ArgumentException("Tailscale ServeのHTTPS URL（パスなし）を指定してください。", nameof(origin));
        return new UriBuilder(Uri.UriSchemeHttps, origin.IdnHost).Uri;
    }
}
