using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using GameLauncherWithGit.Infrastructure.PlayRecordingWeb;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GameLauncherWithGit.PlayRecordingWeb.Tests;

/// <summary>Exercises the production recording listener with isolated loopback sockets and no desktop data.</summary>
public sealed class PlayRecordingWebHostTests
{
    private const string Negotiate = "/_blazor/negotiate?negotiateVersion=1";
    private static readonly Uri ProxyOrigin = new("https://recording.example.invalid/");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>V01: ordinary loopback browsers receive the page and negotiate without credentials, but an unrelated Host is not a direct entry.</summary>
    [Fact]
    public async Task LoopbackAliasesOpenWithoutCredentials()
    {
        await using var product = new PlayRecordingWebHost();
        var local = await product.StartIsolatedAsync();
        using var client = CreateClient(local);
        Assert.Equal("localhost", local.Host);
        Assert.Equal(local, (await product.Startup).Address);

        // Cover the displayed localhost address as well as the numeric loopback alias.
        foreach (var address in new[] { local, new UriBuilder(local) { Host = "127.0.0.1" }.Uri })
        {
            using var root = await client.GetAsync(address);
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
            Assert.Contains("_framework/blazor.web.js", await root.Content.ReadAsStringAsync());
            Assert.True(root.Headers.CacheControl?.NoStore);
            Assert.Contains("frame-ancestors 'none'", root.Headers.GetValues("Content-Security-Policy").Single());
            // A framework antiforgery cookie is not a recording credential.
            if (root.Headers.TryGetValues("Set-Cookie", out var cookies))
                Assert.All(cookies, cookie => Assert.StartsWith("PlayRecording.Antiforgery=", cookie));
            using var negotiation = await SendAsync(client, Negotiate, HttpMethod.Post, address, address.Authority);
            Assert.Equal(HttpStatusCode.OK, negotiation.StatusCode);
        }

        using var unrelatedHost = await SendAsync(client, "/", HttpMethod.Get, host: ProxyOrigin.Authority);
        Assert.Equal(HttpStatusCode.BadRequest, unrelatedHost.StatusCode);
        using var crossOrigin = await SendAsync(client, Negotiate, HttpMethod.Post, ProxyOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);
        using var unspecifiedOrigin = await SendAsync(client, Negotiate, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Forbidden, unspecifiedOrigin.StatusCode);
    }

    /// <summary>V02 substitute: an unregistered browser-facing HTTP origin supports assets and a WebSocket behind a loopback proxy, with same-origin checks intact.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForwardedPageAssetsAndSocketUseTheBrowserOrigin(bool proxyPreservesHost)
    {
        await using var product = new PlayRecordingWebHost();
        var local = await product.StartIsolatedAsync();
        using var client = CreateClient(local);
        var backendHost = proxyPreservesHost ? ProxyOrigin.Authority : local.Authority;
        var forwardedHost = proxyPreservesHost ? null : ProxyOrigin.Authority;
        using var page = await SendAsync(client, "/", HttpMethod.Get, host: backendHost, forwardedScheme: "https", forwardedHost: forwardedHost);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("wss://recording.example.invalid", page.Headers.GetValues("Content-Security-Policy").Single());
        using var asset = await SendAsync(client, "/assets/play-recording.css", HttpMethod.Get,
            host: backendHost, forwardedScheme: "https", forwardedHost: forwardedHost);
        Assert.Equal("text/css", asset.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        using var unrelatedOrigin = await SendAsync(client, Negotiate, HttpMethod.Post, local, backendHost, "https", forwardedHost);
        Assert.Equal(HttpStatusCode.Forbidden, unrelatedOrigin.StatusCode);
        using var brokenForwarding = await SendAsync(client, "/", HttpMethod.Get, host: backendHost,
            forwardedScheme: "https, http", forwardedHost: forwardedHost);
        Assert.Equal(HttpStatusCode.BadRequest, brokenForwarding.StatusCode);

        using var negotiation = await SendAsync(client, Negotiate, HttpMethod.Post, ProxyOrigin, backendHost, "https", forwardedHost);
        negotiation.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await negotiation.Content.ReadAsStringAsync());
        var token = result.RootElement.GetProperty("connectionToken").GetString()!;
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.SetRequestHeader("Host", backendHost);
        socket.Options.SetRequestHeader("Origin", ProxyOrigin.GetLeftPart(UriPartial.Authority));
        socket.Options.SetRequestHeader("X-Forwarded-Proto", "https");
        // Test separately with Host preserved and with the standard forwarded Host.
        if (forwardedHost is not null) socket.Options.SetRequestHeader("X-Forwarded-Host", forwardedHost);
        using var deadline = new CancellationTokenSource(Timeout);
        var endpoint = new UriBuilder(local) { Scheme = "ws", Path = "/_blazor", Query = $"id={Uri.EscapeDataString(token)}" }.Uri;
        await socket.ConnectAsync(endpoint, deadline.Token);
        Assert.Equal(WebSocketState.Open, socket.State);
        socket.Abort();
    }

    /// <summary>V04: a bind conflict reports an actionable failure without publishing a URL; a healthy listener stops on disposal.</summary>
    [Fact]
    public async Task PortConflictReportsFailureAndDisposalClosesHealthyListener()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Server.ExclusiveAddressUse = true;
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var failed = new PlayRecordingWebHost();
        Assert.IsAssignableFrom<IOException>(await Record.ExceptionAsync(() => failed.StartLocalCoreAsync(port)));
        var failedStartup = await failed.Startup.WaitAsync(Timeout);
        Assert.Null(failedStartup.Address);
        Assert.Contains($"ポート {port} は使用中", failedStartup.Failure);

        await using var healthy = new PlayRecordingWebHost();
        var address = await healthy.StartIsolatedAsync();
        using var client = CreateClient(address);
        using var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        await healthy.DisposeAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => healthy.StartIsolatedAsync());
    }

    /// <summary>V04: exit also closes admission when Kestrel startup has not completed, without leaving an unresolved settings state.</summary>
    [Fact]
    public async Task DisposalInterruptsStartupAndIsTerminal()
    {
        var pause = new PausedStartup();
        var content = PlayRecordingWebContent.WithService<IHostedService>(typeof(PlayRecordingMount), pause);
        await using var product = new PlayRecordingWebHost(content);
        var binding = product.StartIsolatedAsync();
        await pause.Entered.Task.WaitAsync(Timeout);
        await product.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.Null((await product.Startup.WaitAsync(Timeout)).Address);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => binding.WaitAsync(Timeout));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => product.StartIsolatedAsync());
    }

    /// <summary>Blocks startup until the real host cancels it.</summary>
    private sealed class PausedStartup : IHostedService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Signals entry and waits for the real startup cancellation.</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
        }

        /// <summary>No resources remain after cancellation.</summary>
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Only contacts the fixture's listener and does not retain cookies or follow redirects.</summary>
    private static HttpClient CreateClient(Uri address) =>
        new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        {
            BaseAddress = address,
            Timeout = Timeout
        };

    /// <summary>Sends a browser or proxy-shaped request to the same isolated transport.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, HttpMethod method,
        Uri? origin = null, string? host = null, string? forwardedScheme = null, string? forwardedHost = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Host = host;
        // Keep network routing local regardless of the synthetic browser-facing authority.
        if (origin is not null) request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        if (forwardedScheme is not null) request.Headers.Add("X-Forwarded-Proto", forwardedScheme);
        if (forwardedHost is not null) request.Headers.Add("X-Forwarded-Host", forwardedHost);
        return await client.SendAsync(request);
    }
}
