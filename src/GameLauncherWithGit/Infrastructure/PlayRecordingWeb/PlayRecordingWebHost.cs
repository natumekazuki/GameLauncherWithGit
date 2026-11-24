using System.Globalization;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Application.Services;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.AspNetCore.Builder;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

/// <summary>Owns the manually started Windows loopback host, not the desktop record services.</summary>
public sealed class PlayRecordingWebHost(
    IGameLibraryStore games,
    IScenarioCaptureStore capture,
    PlayRecordingWebContent? content = null) : IAsyncDisposable
{
    public const int LoopbackPort = 47832;
    private readonly PlayRecordingWebAccess _access = new(TimeProvider.System);
    private readonly SemaphoreSlim _lifecycle = new(1);
    private WebApplication? _app;
    private bool _disposed;

    public Uri? Origin { get; private set; }
    public PlayRecordingWebPermission? Permission => _access.Permission;

    /// <summary>Lists registered games with a confirmed and enabled capture setup; never creates profiles.</summary>
    public async Task<IReadOnlyList<GameCardItem>> GetEligibleGamesAsync()
    {
        var configuredIds = (await capture.GetCaptureProfilesAsync())
            .Where(profile => profile.GameId is not null && profile.HasConfirmedRecordingSetup)
            .Select(profile => profile.GameId!).ToHashSet(StringComparer.Ordinal);
        return (await games.GetAllAsync()).Where(game => configuredIds.Contains(game.Id)).ToArray();
    }

    /// <summary>Starts on the fixed loopback port for the explicitly allowed HTTPS Serve origin.</summary>
    public Task<PlayRecordingWebPermission> StartAsync(string gameId, Uri origin) =>
        StartCoreAsync(gameId, RequireServeOrigin(origin), LoopbackPort, isolated: false);

    /// <summary>Starts a direct HTTP endpoint only when the app uses a dedicated data directory.</summary>
    public Task<PlayRecordingWebPermission> StartIsolatedAsync(string gameId, int port)
    {
        if (!AppDataPaths.IsIsolated)
            throw new InvalidOperationException("Direct loopback requires an isolated data directory.");
        if (port is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        return StartCoreAsync(gameId, new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"),
            port, isolated: true);
    }

    /// <summary>Replaces the current grant and invalidates its cookie and circuits, without restarting the listener.</summary>
    public async Task<PlayRecordingWebPermission> IssueAsync(string gameId)
    {
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_app is null) throw new InvalidOperationException("Web接続が開始されていません。");
            var game = await RequireEligibleGameAsync(gameId);
            await _access.IssueAsync(game.Id, game.Title);
            return Permission!;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Invalidates access and waits for any operation already authorized under the old grant.</summary>
    public async Task RevokeAsync()
    {
        await _lifecycle.WaitAsync();
        try { await _access.RevokeAsync(); }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Revokes authorization and stops only the Web host.</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            _disposed = true;
            await StopCoreAsync();
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<PlayRecordingWebPermission> StartCoreAsync(string gameId, Uri origin, int port, bool isolated)
    {
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_app is not null) throw new InvalidOperationException("Web接続はすでに開始されています。");
            var game = await RequireEligibleGameAsync(gameId);
            var app = PlayRecordingWebApplication.Build(origin, port, isolated, _access, content);
            try
            {
                await app.StartAsync();
                await _access.IssueAsync(game.Id, game.Title);
                _app = app;
                Origin = origin;
                return Permission!;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        await _access.RevokeAsync();
        if (_app is not { } app) return;

        _app = null;
        Origin = null;
        try { await app.StopAsync(); }
        finally { await app.DisposeAsync(); }
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
