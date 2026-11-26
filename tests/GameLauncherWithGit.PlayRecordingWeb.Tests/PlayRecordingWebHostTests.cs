using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using GameLauncherWithGit.Infrastructure.PlayRecordingWeb;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GameLauncherWithGit.PlayRecordingWeb.Tests;

public sealed class PlayRecordingWebHostTests
{
    private static readonly Uri ServeOrigin = new("https://desktop.synthetic.ts.net/");
    private const string Negotiate = "/_blazor/negotiate?negotiateVersion=1";

    // @test-value v2
    // kind = "invariant"
    // claim = "Only direct same-origin traffic on localhost or 127.0.0.1 is admitted to the code-free recording listener."
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / play-web-local-host-lifecycle and local-recording-web-follows-pc-play" }
    // fault = "An ordinary local browser is required to log in, or a request with an unrecognized Host, proxy metadata or unsuitable Origin reaches the local circuit endpoint."
    // observable = "Both loopback aliases serve the recording root and accept same-origin negotiation without credentials; the local login and disallowed requests return client errors."
    // observation_boundary = "public-boundary"
    // scope = "recording-web-local-http-admission"
    // lifecycle = "permanent"
    // risk_tags = ["authentication", "authorization"]
    // @end-test-value
    [Fact]
    public async Task DirectLoopbackAliasesUseNoCodeAndRejectOtherOrigins()
    {
        await using var host = await RunningHost.StartAsync();
        var localhost = new UriBuilder(host.LocalOrigin) { Host = "localhost" }.Uri;

        foreach (var origin in new[] { host.LocalOrigin, localhost })
        {
            using var root = await host.SendAsync(origin, "/");
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
            if (root.Headers.TryGetValues("Set-Cookie", out var cookies))
                Assert.DoesNotContain(cookies, value => value.StartsWith("__Host-PlayRecording.Permission=", StringComparison.Ordinal));
            using var negotiate = await host.SendAsync(origin, Negotiate, HttpMethod.Post, origin);
            Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
            using var login = await host.SendAsync(origin, "/login");
            AssertClientRejection(login);
        }

        using var unknownHost = await host.SendAsync(new UriBuilder(host.LocalOrigin) { Host = "unrecognized.invalid" }.Uri, "/");
        AssertClientRejection(unknownHost);
        using var forwarded = await host.SendAsync(host.LocalOrigin, "/", forwardedByServe: true);
        AssertClientRejection(forwarded);
        using var otherOrigin = await host.SendAsync(localhost, Negotiate, HttpMethod.Post, host.LocalOrigin);
        AssertClientRejection(otherOrigin);
        using var externalOrigin = await host.SendAsync(localhost, Negotiate, HttpMethod.Post, ServeOrigin);
        AssertClientRejection(externalOrigin);
        using var noOrigin = await host.SendAsync(localhost, Negotiate, HttpMethod.Post);
        AssertClientRejection(noOrigin);
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "Private Serve on the loopback listener still requires the configured HTTPS origin, antiforgery, a one-time code and a current grant; disabling it leaves the local path available."
    // oracle = { type = "contract", ref = "README.md: プレイ中の記録をスマホ・タブレットで操作する（Windows）; WithMate Session Graph play-recording-web / play-web-local-host-lifecycle; PlayRecordingWebApplication.cs: SignInAsync contract" }
    // fault = "The loopback proxy is treated as a local browser, an incorrect origin or unauthenticated request can open a Serve circuit, or a revoked code or cookie remains usable."
    // observable = "Serve login redirects, secure cookie issuance and client errors for the wrong origin or host, missing antiforgery, revoked cookie and disabled origin; a reused code issues no cookie. Local reads succeed after stopping Serve, but fail to connect after disposal."
    // observation_boundary = "public-boundary"
    // scope = "recording-web-serve-http-admission"
    // lifecycle = "permanent"
    // risk_tags = ["authentication", "authorization"]
    // @end-test-value
    [Fact]
    public async Task ServeKeepsItsOriginCodeAndRevocationBoundaryOnTheSharedListener()
    {
        await using var host = await RunningHost.StartAsync();
        var permission = await host.Product.StartServeAsync(FixtureStore.Game.Id, ServeOrigin);
        Assert.Equal(FixtureStore.Game.Id, permission.GameId);
        var code = permission.Code!;

        using var anonymousRoot = await host.SendAsync(ServeOrigin, "/", forwardedByServe: true);
        Assert.Equal(HttpStatusCode.Redirect, anonymousRoot.StatusCode);
        Assert.Equal("/login", anonymousRoot.Headers.Location?.OriginalString);
        using var anonymousHub = await host.SendAsync(ServeOrigin, Negotiate, HttpMethod.Post, ServeOrigin, forwardedByServe: true);
        AssertClientRejection(anonymousHub);

        using var login = await host.SendAsync(ServeOrigin, "/login", forwardedByServe: true);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var antiforgeryCookie = ReadSecureCookie(login, "__Host-PlayRecording.Antiforgery=");
        var html = await login.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        using var noToken = await host.SendAsync(ServeOrigin, "/login", HttpMethod.Post, ServeOrigin, antiforgeryCookie,
            LoginFields(code, ""), forwardedByServe: true);
        AssertClientRejection(noToken);
        using var crossOrigin = await host.SendAsync(ServeOrigin, "/login", HttpMethod.Post, host.LocalOrigin, antiforgeryCookie,
            LoginFields(code, token), forwardedByServe: true);
        AssertClientRejection(crossOrigin);
        using var accepted = await host.SendAsync(ServeOrigin, "/login", HttpMethod.Post, ServeOrigin, antiforgeryCookie,
            LoginFields(code, token), forwardedByServe: true);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Equal("/", accepted.Headers.Location?.OriginalString);
        var sessionCookie = ReadSecureCookie(accepted, "__Host-PlayRecording.Permission=");

        using var reused = await host.SendAsync(ServeOrigin, "/login", HttpMethod.Post, ServeOrigin, antiforgeryCookie,
            LoginFields(code, token), forwardedByServe: true);
        Assert.Equal(HttpStatusCode.Redirect, reused.StatusCode);
        Assert.StartsWith("/login", reused.Headers.Location?.OriginalString);
        if (reused.Headers.TryGetValues("Set-Cookie", out var reusedCookies))
            Assert.DoesNotContain(reusedCookies, value => value.StartsWith("__Host-PlayRecording.Permission=", StringComparison.Ordinal));
        using var authorized = await host.SendAsync(ServeOrigin, Negotiate, HttpMethod.Post, ServeOrigin, sessionCookie, forwardedByServe: true);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        using var authorizedRoot = await host.SendAsync(ServeOrigin, "/", cookie: sessionCookie, forwardedByServe: true);
        Assert.Equal(HttpStatusCode.OK, authorizedRoot.StatusCode);
        using var wrongOrigin = await host.SendAsync(ServeOrigin, Negotiate, HttpMethod.Post, host.LocalOrigin, sessionCookie, forwardedByServe: true);
        AssertClientRejection(wrongOrigin);
        var otherAuthority = new Uri("https://other.synthetic.ts.net/");
        using var wrongHost = await host.SendAsync(otherAuthority, Negotiate, HttpMethod.Post, otherAuthority, sessionCookie, forwardedByServe: true);
        AssertClientRejection(wrongHost);

        await host.Product.RevokeAsync();
        using var revoked = await host.SendAsync(ServeOrigin, Negotiate, HttpMethod.Post, ServeOrigin, sessionCookie, forwardedByServe: true);
        AssertClientRejection(revoked);
        await host.Product.StopServeAsync();
        using var disabled = await host.SendAsync(ServeOrigin, "/", forwardedByServe: true);
        AssertClientRejection(disabled);
        using var local = await host.SendAsync(host.LocalOrigin, "/");
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        await host.Product.DisposeAsync();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var _ = await host.SendAsync(host.LocalOrigin, "/");
        });
    }

    // @test-value v2
    // kind = "invariant"
    // claim = "Desktop disposal is terminal even when startup races with it, and does not wait for a pending Serve game lookup."
    // oracle = { type = "contract", ref = "WithMate Session Graph play-recording-web / play-web-tray-exit-order and play-web-local-host-lifecycle; PlayRecordingWebHost.cs: DisposeAsync contract" }
    // fault = "A concurrent start leaves the local listener reachable after disposal, or a blocked Serve setting holds the desktop shutdown gate."
    // observable = "Disposal completes while the lookup is held, that lookup cannot issue a grant afterward, concurrent startup either stops its bound listener or fails, and a new start after disposal is rejected."
    // observation_boundary = "public-boundary"
    // scope = "recording-web-desktop-lifetime"
    // lifecycle = "permanent"
    // @end-test-value
    [Fact]
    public async Task DesktopDisposalIsTerminalAndDoesNotWaitForServeSetup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GameCardItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await RunningHost.StartAsync(() =>
        {
            entered.SetResult();
            return release.Task;
        });
        var enablingServe = host.Product.StartServeAsync(FixtureStore.Game.Id, ServeOrigin);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await host.Product.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Null(host.Product.ServeOrigin);
            Assert.Null(host.Product.Permission);
        }
        finally { release.TrySetResult(FixtureStore.Game); }
        var serveFailure = await Record.ExceptionAsync(() => enablingServe);
        Assert.True(serveFailure is ObjectDisposedException or OperationCanceledException, serveFailure?.ToString());
        Assert.Null(host.Product.ServeOrigin);
        Assert.Null(host.Product.Permission);

        var pause = new PausedStartup();
        var content = PlayRecordingWebContent.WithService<IHostedService>(typeof(PlayRecordingMount), pause);
        await using var racing = RunningHost.CreateProduct(content: content);
        var startup = racing.StartIsolatedAsync();
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await racing.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally { pause.Release.TrySetResult(); }
        Uri? startedAt = null;
        var startupFailure = await Record.ExceptionAsync(async () => startedAt = await startup.WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.True(startupFailure is null or ObjectDisposedException or OperationCanceledException, startupFailure?.ToString());
        if (startedAt is not null)
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
            {
                using var _ = await client.GetAsync(startedAt);
            });
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => racing.StartIsolatedAsync());
    }

    private sealed class PausedStartup : IHostedService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static FormUrlEncodedContent LoginFields(string code, string token) => new(new Dictionary<string, string>
    {
        ["code"] = code,
        ["__RequestVerificationToken"] = token
    });

    private static string ReadSecureCookie(HttpResponseMessage response, string prefix)
    {
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        return cookie.Split(';')[0];
    }

    private static void AssertClientRejection(HttpResponseMessage response) => Assert.InRange((int)response.StatusCode, 400, 499);

    public class FixtureStore : DispatchProxy
    {
        internal static readonly GameCardItem Game = new("game-one", "Synthetic game", "synthetic.exe", null, null, null, GameCardStatus.Unknown);
        private static readonly CaptureProfile Profile = new(1, Game.Id, Game.Title, true, Game.ExecutablePath,
            new() { Selections = [new("Synthetic", "Synthetic", 0, 0, CaptureRole.Body)] });
        internal Func<Task<GameCardItem?>>? Lookup { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IGameLibraryStore.FindByIdAsync) when (string?)args?[0] == Game.Id => Lookup?.Invoke() ?? Task.FromResult<GameCardItem?>(Game),
            nameof(IGameLibraryStore.FindByIdAsync) => Task.FromResult<GameCardItem?>(null),
            nameof(IScenarioCaptureStore.FindCaptureProfileAsync) => Task.FromResult((string?)args?[0] == Game.Id ? Profile : null),
            _ => throw new InvalidOperationException($"Unexpected fixture store access: {method?.Name}.")
        };
    }

    private sealed class RunningHost(PlayRecordingWebHost product, Uri origin) : IAsyncDisposable
    {
        private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
            { Timeout = TimeSpan.FromSeconds(5) };
        public PlayRecordingWebHost Product { get; } = product;
        public Uri LocalOrigin { get; } = origin;

        public static PlayRecordingWebHost CreateProduct(Func<Task<GameCardItem?>>? lookup = null, PlayRecordingWebContent? content = null)
        {
            var games = DispatchProxy.Create<IGameLibraryStore, FixtureStore>();
            ((FixtureStore)games).Lookup = lookup;
            return new(games, DispatchProxy.Create<IScenarioCaptureStore, FixtureStore>(), content);
        }

        public static async Task<RunningHost> StartAsync(Func<Task<GameCardItem?>>? lookup = null)
        {
            var host = CreateProduct(lookup);
            try
            {
                return new(host, await host.StartIsolatedAsync());
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public Task<HttpResponseMessage> SendAsync(Uri authority, string path, HttpMethod? method = null, Uri? origin = null,
            string? cookie = null, HttpContent? content = null, bool forwardedByServe = false) =>
            SendCoreAsync(authority, path, method ?? HttpMethod.Get, origin, cookie, content, forwardedByServe);

        private async Task<HttpResponseMessage> SendCoreAsync(Uri authority, string path, HttpMethod method, Uri? origin,
            string? cookie, HttpContent? content, bool forwardedByServe)
        {
            // Send only to the isolated listener, including synthetic Serve requests. The
            // local alias itself is dialed by name so its DNS/loopback path is covered too.
            using var request = new HttpRequestMessage(method, new Uri(authority.Host == "localhost" ? authority : LocalOrigin, path))
            {
                Content = content
            };
            request.Headers.Host = authority.Authority;
            if (origin is not null) request.Headers.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            if (forwardedByServe) request.Headers.Add("X-Forwarded-Host", ServeOrigin.Authority);
            return await _http.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await Product.DisposeAsync();
        }
    }
}
