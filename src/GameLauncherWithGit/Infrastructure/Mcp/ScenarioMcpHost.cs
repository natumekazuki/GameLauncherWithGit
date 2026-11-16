using System.Net;
using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace GameLauncherWithGit.Infrastructure.Mcp;

/// <summary>Application-owned, opt-in loopback listener. No transport session carries authorization.</summary>
public sealed class ScenarioMcpHost : IAsyncDisposable
{
    internal static readonly object AccessLevelKey = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _application;
    public Uri? Endpoint { get; private set; }

    public async Task StartAsync(ScenarioMcpConfiguration configuration, IScenarioCaptureStore captures,
        IScenarioMaterialReader materials, IScenarioScreenshotStore screenshots,
        CancellationToken cancellationToken = default, bool useEphemeralPort = false)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_application is not null) return;
            configuration.Validate();
            if (!configuration.Enabled) return;
            // Freeze credentials for this listener lifetime; configuration edits apply on restart.
            var keys = new ScenarioMcpConfiguration
            {
                Enabled = true, Port = configuration.Port,
                StructureKey = configuration.StructureKey, FullKey = configuration.FullKey
            };
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [], EnvironmentName = Environments.Production
            });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, useEphemeralPort ? 0 : keys.Port);
                options.Limits.MaxRequestBodySize = 1024 * 1024;
            });
            // The desktop app, not console signals or a second service, owns shutdown.
            builder.Services.AddSingleton<IHostLifetime, DesktopHostLifetime>();
            var context = new HttpContextAccessor();
            builder.Services.AddSingleton<IHttpContextAccessor>(context);
            var endpoints = new ScenarioMcpEndpoints(context, captures, materials, screenshots);
            builder.Services.AddMcpServer().WithHttpTransport(options =>
            {
                options.SessionMode = HttpServerSessionMode.Stateless;
            }).WithTools(endpoints).WithResources(endpoints);
            var app = builder.Build();
            app.Use(async (http, next) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                if (!IsLocalHost(http.Request.Host.Host) ||
                    (http.Request.Host.Port ?? 80) != http.Connection.LocalPort ||
                    !IsAllowedOrigin(http.Request))
                {
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                var headers = http.Request.Headers.Authorization;
                var level = headers.Count == 1 ? keys.Authenticate(headers[0]) : null;
                if (level is null)
                {
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    http.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }
                http.Items[AccessLevelKey] = level.Value;
                try { await next(http); }
                catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
                catch
                {
                    // Never return raw protocol arguments, credentials, paths, or store exceptions.
                    if (!http.Response.HasStarted) { http.Response.Clear(); http.Response.StatusCode = 500; }
                    else http.Abort();
                }
            });
            app.MapMcp("/mcp");
            try
            {
                await app.StartAsync(cancellationToken);
                Endpoint = new Uri(app.Urls.Single().TrimEnd('/') + "/mcp");
                _application = app;
            }
            catch { await app.DisposeAsync(); throw new InvalidOperationException("Scenario MCP could not start."); }
        }
        finally { _lifecycle.Release(); }
    }

    private static bool IsLocalHost(string host) => host is "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    private static bool IsAllowedOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var origins)) return true;
        return origins.Count == 1 && Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            && origin.Scheme == "http" && IsLocalHost(origin.Host)
            && origin.Port == request.HttpContext.Connection.LocalPort
            && origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && origin.UserInfo.Length == 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_application is not { } app) return;
            _application = null;
            Endpoint = null;
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(timeout.Token); }
            finally { await app.DisposeAsync(); }
        }
        finally { _lifecycle.Release(); }
    }

    private sealed class DesktopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
