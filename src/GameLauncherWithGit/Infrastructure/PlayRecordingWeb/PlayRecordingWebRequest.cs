using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

/// <summary>The browser origin admitted by the loopback listener, before authentication or routing.</summary>
internal sealed record PlayRecordingWebRequest(Uri Origin, bool IsLocal)
{
    private static readonly string[] ForwardedHeaders =
    [
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port",
        "X-Forwarded-Prefix", "Tailscale-User-Login", "Tailscale-User-Name", "Tailscale-User-Profile-Pic",
        "Tailscale-Headers-Info", "Tailscale-App-Capabilities"
    ];

    public static PlayRecordingWebRequest? Resolve(HttpContext context, Uri? serveOrigin)
    {
        var request = context.Request;
        if (context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer)
            || request.Headers.ContainsKey("Tailscale-Funnel-Request"))
            return null;

        // Serve preserves the external Host and also emits forwarding headers. Neither the
        // loopback peer nor forwarded metadata is a credential for the direct browser path.
        var host = request.Host.Value;
        if (serveOrigin is not null && string.Equals(host, serveOrigin.Authority, StringComparison.OrdinalIgnoreCase))
            return new(serveOrigin, IsLocal: false);

        var port = context.Connection.LocalPort.ToString(CultureInfo.InvariantCulture);
        if (ForwardedHeaders.Any(request.Headers.ContainsKey)) return null;
        if (string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase))
            return new(new Uri($"http://localhost:{port}/"), IsLocal: true);
        if (string.Equals(host, $"127.0.0.1:{port}", StringComparison.Ordinal))
            return new(new Uri($"http://127.0.0.1:{port}/"), IsLocal: true);
        return null;
    }

    public bool AllowsOrigin(HttpRequest request)
    {
        var supplied = request.Headers.Origin;
        var safeMethod = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        // Asset and initializer reads do not send Origin; negotiation and WebSockets do.
        var required = !safeMethod || (request.Path.StartsWithSegments("/_blazor")
            && request.Path != "/_blazor/initializers");
        return !required && supplied.Count == 0
            || supplied.Count == 1 && supplied[0] == Origin.GetLeftPart(UriPartial.Authority);
    }
}
