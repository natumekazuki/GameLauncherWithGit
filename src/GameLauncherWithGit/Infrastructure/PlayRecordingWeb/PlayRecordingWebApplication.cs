using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HeaderNames = Microsoft.Net.Http.Headers.HeaderNames;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

internal static class PlayRecordingWebApplication
{
    private const string ServeScheme = "PlayRecording";
    private const string LocalScheme = "PlayRecordingLocal";
    private const string AdmissionScheme = "PlayRecordingAdmission";
    private const string LoginPath = "/login";

    public static WebApplication Build(int port, PlayRecordingWebAccess access, PlayRecordingWebContent? content)
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
            // Kestrel does not support a dynamic port with ListenLocalhost. The isolated
            // fixture binds atomically on IPv4; localhost resolves to that loopback too.
            if (port == 0) options.Listen(IPAddress.Loopback, port);
            else options.ListenLocalhost(port);
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 32 * 1024;
        });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));

        const string cookiePrefix = "__Host-PlayRecording.";
        builder.Services.AddDataProtection()
            .UseEphemeralDataProtectionProvider()
            // ASP.NET Core also preloads its default key ring at startup. Keep that ring
            // in memory as well, instead of letting it discover the user profile or registry.
            .AddKeyManagementOptions(options => options.XmlRepository = new MemoryKeyRepository());
        builder.Services.AddAuthentication(AdmissionScheme)
            .AddPolicyScheme(AdmissionScheme, null, options => options.ForwardDefaultSelector = context =>
                context.Features.Get<PlayRecordingWebRequest>()?.IsLocal == true ? LocalScheme : ServeScheme)
            .AddScheme<AuthenticationSchemeOptions, LocalAuthenticationHandler>(LocalScheme, _ => { })
            .AddCookie(ServeScheme, options =>
        {
            options.Cookie.Name = cookiePrefix + "Permission";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.IsEssential = true;
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = context =>
            {
                if (context.HttpContext.Features.Get<PlayRecordingWebRequest>() is not { IsLocal: false } request
                    || !access.IsCurrent(PlayRecordingWebAccess.ReadEpoch(context.Principal), request.Origin))
                    context.RejectPrincipal();
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path == "/" && HttpMethods.IsGet(context.Request.Method))
                    context.Response.Redirect(LoginPath);
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
            options.Cookie = new RecordingAntiforgeryCookie
            {
                Name = cookiePrefix + "Antiforgery",
                Path = "/",
                // Razor renders an antiforgery token even on the form-free local GET.
                // Allow that render over loopback HTTP without weakening the Serve cookie.
                SecurePolicy = CookieSecurePolicy.SameAsRequest,
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true
            };
        });
        builder.Services.AddRouting();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
        {
            options.DetailedErrors = false;
            options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(access);
        builder.Services.AddScoped(_ => new PlayRecordingWebSession(access));
        builder.Services.AddScoped<CircuitHandler, PlayRecordingWebCircuitHandler>();
        builder.Services.AddSingleton(new PlayRecordingWebView(content?.PanelType,
            typeof(PlayRecordingWebApplication).Assembly.GetManifestResourceInfo("PlayRecordingWeb.play-recording-panel.css") is not null));
        content?.Register(builder.Services);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var admission = PlayRecordingWebRequest.Resolve(context, access.ServeOrigin);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
                // no-referrer would also turn a same-origin HTML form POST's Origin into "null".
                context.Response.Headers["Referrer-Policy"] = "same-origin";
                if (admission is { } accepted)
                    context.Response.Headers[HeaderNames.ContentSecurityPolicy] =
                        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; "
                        + $"form-action {accepted.Origin.GetLeftPart(UriPartial.Authority)}; img-src 'self' data:; style-src 'self'; "
                        + $"connect-src 'self' {(accepted.IsLocal ? "ws" : "wss")}://{accepted.Origin.Authority}";
                return Task.CompletedTask;
            });

            var request = context.Request;
            if (access.IsClosed)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            if (admission is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!admission.AllowsOrigin(request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (admission.IsLocal && request.Path.StartsWithSegments(LoginPath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Serve terminates TLS. Restore only the scheme of the validated browser origin.
            context.Features.Set(admission);
            request.Scheme = admission.Origin.Scheme;
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

    /// <summary>Redeems the Serve-only code only after antiforgery validation for that browser.</summary>
    private static async Task<IResult> SignInAsync(HttpContext context, IAntiforgery antiforgery, PlayRecordingWebAccess access)
    {
        if (context.Features.Get<PlayRecordingWebRequest>() is not { IsLocal: false } request)
            return Results.NotFound();
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
        if (code.Count != 1 || await access.RedeemAsync(code[0]!, request.Origin) is not { } grant)
            return Results.LocalRedirect(LoginPath + "?error=code");

        var identity = new ClaimsIdentity([new Claim(PlayRecordingWebAccess.EpochClaim, grant.Epoch)], ServeScheme);
        await context.SignInAsync(ServeScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = grant.ExpiresAt, IsPersistent = false, AllowRefresh = false });
        return Results.LocalRedirect("/");
    }

    private sealed class LocalAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Context.Features.Get<PlayRecordingWebRequest>() is { IsLocal: true } request
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, request.Origin.Authority)], LocalScheme)), LocalScheme))
                : AuthenticateResult.NoResult());
    }

    private sealed class RecordingAntiforgeryCookie : CookieBuilder
    {
        public override CookieOptions Build(HttpContext context, DateTimeOffset expiresFrom)
        {
            var cookie = base.Build(context, expiresFrom);
            cookie.Secure = true;
            return cookie;
        }
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
