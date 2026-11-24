using System.Net;
using System.Security.Claims;
using System.Xml.Linq;
using GameLauncherWithGit.PlayRecordingWeb;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

internal static class PlayRecordingWebApplication
{
    private const string Scheme = "PlayRecording";
    private const string LoginPath = "/login";

    public static WebApplication Build(Uri origin, int port, bool isolated,
        PlayRecordingWebAccess access, PlayRecordingWebContent? content)
    {
        // The empty builder does not import Kestrel, hosting startup or forwarded-header
        // configuration from the desktop process environment.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(PlayRecordingRoot).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 32 * 1024;
        });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));

        var cookiePrefix = isolated ? "PlayRecording.Isolated." : "__Host-PlayRecording.";
        var secure = isolated ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
        builder.Services.AddDataProtection()
            .UseEphemeralDataProtectionProvider()
            // ASP.NET Core also preloads its default key ring at startup. Keep that ring
            // in memory as well, instead of letting it discover the user profile or registry.
            .AddKeyManagementOptions(options => options.XmlRepository = new MemoryKeyRepository());
        builder.Services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = cookiePrefix + "Permission";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = secure;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.IsEssential = true;
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = context =>
            {
                if (!access.IsCurrent(PlayRecordingWebAccess.ReadEpoch(context.Principal)))
                    context.RejectPrincipal();
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path == "/" && HttpMethods.IsGet(context.Request.Method))
                    context.Response.Redirect(new Uri(origin, LoginPath).AbsoluteUri);
                else
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization(options => options.FallbackPolicy =
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = cookiePrefix + "Antiforgery";
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = secure;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.IsEssential = true;
        });
        builder.Services.AddRouting();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
        {
            options.DetailedErrors = false;
            options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddSingleton(access);
        builder.Services.AddScoped(_ => new PlayRecordingWebSession(access));
        builder.Services.AddScoped<CircuitHandler, PlayRecordingWebCircuitHandler>();
        builder.Services.AddSingleton(new PlayRecordingWebView(origin, content?.PanelType,
            typeof(PlayRecordingWebApplication).Assembly.GetManifestResourceInfo("PlayRecordingWeb.play-recording-panel.css") is not null));
        content?.Register(builder.Services);

        var app = builder.Build();
        var expectedOrigin = origin.GetLeftPart(UriPartial.Authority);
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
                // no-referrer would also turn a same-origin HTML form POST's Origin into "null".
                context.Response.Headers["Referrer-Policy"] = "same-origin";
                context.Response.Headers[HeaderNames.ContentSecurityPolicy] =
                    $"default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; "
                    + $"form-action {expectedOrigin}; img-src 'self' data:; style-src 'self'; "
                    + $"connect-src 'self' {(isolated ? "ws" : "wss")}://{origin.Authority}";
                return Task.CompletedTask;
            });

            var request = context.Request;
            if (context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer)
                || !string.Equals(request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase)
                || request.Headers.ContainsKey("Tailscale-Funnel-Request"))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var suppliedOrigin = request.Headers.Origin;
            // The read-only Blazor initializer GET, like other same-origin asset fetches,
            // has no Origin header in browsers. SignalR negotiation and transports still require it.
            var safeMethod = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
            var originRequired = !safeMethod || (request.Path.StartsWithSegments("/_blazor")
                && request.Path != "/_blazor/initializers");
            if ((originRequired || suppliedOrigin.Count != 0)
                && (suppliedOrigin.Count != 1 || !string.Equals(suppliedOrigin[0], expectedOrigin, StringComparison.Ordinal)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // Browser origin is an explicit PC choice, not X-Forwarded-* or proxy identity.
            // In normal mode TLS terminates at Serve; only the OS-loopback hop is HTTP.
            request.Scheme = origin.Scheme;
            await next(context);
        });

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapPost(LoginPath, (HttpContext context, IAntiforgery antiforgery) =>
            SignInAsync(context, antiforgery, access)).AllowAnonymous();
        MapAsset(app, "play-recording.css", "text/css; charset=utf-8").AllowAnonymous();
        MapAsset(app, "play-recording.js", "text/javascript; charset=utf-8");
        MapScopedStyle(app, "capture-tags.css");
        MapScopedStyle(app, "scenario-flow.css");
        MapScopedStyle(app, "play-recording-panel.css");

        var components = app.MapRazorComponents<PlayRecordingRoot>().AddInteractiveServerRenderMode(options =>
        {
            options.DisableWebSocketCompression = true;
            options.ContentSecurityFrameAncestorsPolicy = null;
        });
        components.Add(endpoint =>
        {
            // The only Web form posts to the explicit antiforgery-checked handler above.
            if (endpoint.Metadata.OfType<ComponentTypeMetadata>().Any())
                endpoint.Metadata.Add(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
        });
        return app;
    }

    private static RouteHandlerBuilder MapAsset(WebApplication app, string file, string contentType) =>
        app.MapGet($"/assets/{file}", () => Results.File(
            typeof(PlayRecordingRoot).Assembly.GetManifestResourceStream($"GameLauncherWithGit.PlayRecordingWeb.Assets.{file}")
                ?? throw new InvalidOperationException($"Missing embedded Web host asset: {file}."),
            contentType));

    private static void MapScopedStyle(WebApplication app, string file)
    {
        var assembly = typeof(PlayRecordingWebApplication).Assembly;
        var resourceName = $"PlayRecordingWeb.{file}";
        if (assembly.GetManifestResourceInfo(resourceName) is not null)
            app.MapGet($"/assets/{file}", () => Results.File(assembly.GetManifestResourceStream(resourceName)!, "text/css; charset=utf-8"));
    }

    private static async Task<IResult> SignInAsync(HttpContext context, IAntiforgery antiforgery, PlayRecordingWebAccess access)
    {
        if (!context.Request.HasFormContentType)
            return Results.BadRequest();
        IFormCollection form;
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is AntiforgeryValidationException or InvalidDataException or BadHttpRequestException)
        {
            return Results.BadRequest();
        }
        var code = form["code"];
        if (code.Count != 1 || await access.RedeemAsync(code[0]!) is not { } grant)
            return Results.LocalRedirect(LoginPath + "?error=code");

        var identity = new ClaimsIdentity([new Claim(PlayRecordingWebAccess.EpochClaim, grant.Epoch)], Scheme);
        await context.SignInAsync(Scheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = grant.ExpiresAt, IsPersistent = false, AllowRefresh = false });
        return Results.LocalRedirect("/");
    }

    private sealed class MemoryKeyRepository : IXmlRepository
    {
        private readonly List<XElement> _keys = [];

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_keys) return _keys.ToArray();
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_keys) _keys.Add(element);
        }
    }
}
