using System.Net;
using System.Xml.Linq;
using GameLauncherWithGit.PlayRecordingWeb;
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

/// <summary>Builds the recording UI's loopback listener and Blazor transport.</summary>
internal static class PlayRecordingWebApplication
{
    /// <summary>Creates an isolated HTTP application exposing only the recording UI and its assets.</summary>
    public static WebApplication Build(int port, CancellationToken closing, PlayRecordingWebContent? content)
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

        // Keep framework antiforgery and Blazor protection separate from access management.
        builder.Services.AddDataProtection()
            .UseEphemeralDataProtectionProvider()
            // Keep the default key ring's startup preload out of the user profile and registry.
            .AddKeyManagementOptions(options => options.XmlRepository = new MemoryKeyRepository());
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "PlayRecording.Antiforgery";
            options.Cookie.Path = "/";
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddRouting();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
        {
            options.DetailedErrors = false;
            options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped(_ => new PlayRecordingWebSession(closing));
        builder.Services.AddScoped<CircuitHandler, PlayRecordingWebCircuitHandler>();
        builder.Services.AddSingleton(new PlayRecordingWebView(content?.PanelType,
            typeof(PlayRecordingWebApplication).Assembly.GetManifestResourceInfo("PlayRecordingWeb.play-recording-panel.css") is not null));
        content?.Register(builder.Services);

        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            var admission = PlayRecordingWebRequest.Resolve(context);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "same-origin";
                if (admission is { } accepted)
                    context.Response.Headers[HeaderNames.ContentSecurityPolicy] =
                        "default-src 'self'; base-uri 'self'; frame-ancestors 'none'; object-src 'none'; "
                        + "form-action 'self'; img-src 'self' data:; style-src 'self'; "
                        + $"connect-src 'self' {(accepted.Origin.Scheme == "https" ? "wss" : "ws")}://{accepted.Origin.Authority}";
                return Task.CompletedTask;
            });

            var request = context.Request;
            // Do not admit new requests while desktop shutdown drains the listener.
            if (closing.IsCancellationRequested)
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

            // Blazor and antiforgery need the validated browser-facing scheme and authority.
            context.Features.Set(admission);
            request.Scheme = admission.Origin.Scheme;
            request.Host = HostString.FromUriComponent(admission.Origin);
            await next(context);
        });

        app.UseRouting();
        app.UseAntiforgery();
        MapAsset(app, "play-recording.css", "text/css; charset=utf-8");
        MapAsset(app, "play-recording.js", "text/javascript; charset=utf-8");
        MapDesktopAsset(app, "scenario-live-feed.js", "text/javascript; charset=utf-8");
        MapDesktopStyle(app, "scenario-shared.css");
        MapDesktopStyle(app, "capture-tags.css");
        MapDesktopStyle(app, "scenario-flow.css");
        MapDesktopStyle(app, "play-recording-panel.css");

        var components = app.MapRazorComponents<PlayRecordingRoot>().AddInteractiveServerRenderMode(options =>
        {
            options.DisableWebSocketCompression = true;
            options.ContentSecurityFrameAncestorsPolicy = null;
        });
        components.Add(endpoint =>
        {
            // The page has no HTTP form handlers; SignalR owns interactive commands.
            if (endpoint.Metadata.OfType<ComponentTypeMetadata>().Any())
                endpoint.Metadata.Add(new HttpMethodMetadata([HttpMethods.Get, HttpMethods.Head]));
        });
        return app;
    }

    /// <summary>Serves an explicitly listed asset from the Web UI assembly.</summary>
    private static RouteHandlerBuilder MapAsset(WebApplication app, string file, string contentType) =>
        app.MapGet($"/assets/{file}", () => Results.File(
            typeof(PlayRecordingRoot).Assembly.GetManifestResourceStream($"GameLauncherWithGit.PlayRecordingWeb.Assets.{file}")
                ?? throw new InvalidOperationException($"Missing embedded Web host asset: {file}."),
            contentType));

    /// <summary>Exposes only the named stylesheet when embedded by the desktop build.</summary>
    private static void MapDesktopStyle(WebApplication app, string file) =>
        MapDesktopAsset(app, file, "text/css; charset=utf-8");

    /// <summary>Serves explicitly embedded desktop assets without exposing other desktop files.</summary>
    private static void MapDesktopAsset(WebApplication app, string file, string contentType)
    {
        var assembly = typeof(PlayRecordingWebApplication).Assembly;
        var resourceName = $"PlayRecordingWeb.{file}";
        if (assembly.GetManifestResourceInfo(resourceName) is not null)
            app.MapGet($"/assets/{file}", () => Results.File(assembly.GetManifestResourceStream(resourceName)!, contentType));
    }

    /// <summary>Retains framework keys only for the listener lifetime.</summary>
    private sealed class MemoryKeyRepository : IXmlRepository
    {
        private readonly List<XElement> _keys = [];

        /// <summary>Returns a snapshot of the in-memory keys.</summary>
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_keys) return _keys.ToArray();
        }

        /// <summary>Stores a key without persisting it to disk.</summary>
        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_keys) _keys.Add(element);
        }
    }
}
