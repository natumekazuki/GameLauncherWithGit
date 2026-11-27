using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace GameLauncherWithGit.Infrastructure.PlayRecordingWeb;

/// <summary>The browser-facing origin of a request received on the loopback socket.</summary>
internal sealed record PlayRecordingWebRequest(Uri Origin)
{
    /// <summary>Accepts the local Host or an origin supplied by the immediate loopback HTTP proxy.</summary>
    public static PlayRecordingWebRequest? Resolve(HttpContext context)
    {
        if (context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer))
            return null;

        var request = context.Request;
        var proto = request.Headers["X-Forwarded-Proto"];
        var forwardedHost = request.Headers["X-Forwarded-Host"];
        if (proto.Count == 0 && forwardedHost.Count == 0)
        {
            // A direct browser cannot select an arbitrary Host on the loopback listener.
            var port = context.Connection.LocalPort.ToString(CultureInfo.InvariantCulture);
            var host = request.Host.Value;
            if (string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase)
                || host == $"127.0.0.1:{port}" || host == $"[::1]:{port}")
                return new(new Uri($"http://{host}/"));
            return null;
        }

        // The operator-controlled proxy must overwrite these headers, not pass client values
        // through. Ignore other metadata, including the original client address.
        if (proto.Count != 1 || proto[0] is not ("http" or "https") || forwardedHost.Count > 1)
            return null;
        var authority = forwardedHost.Count == 1 ? forwardedHost[0] : request.Host.Value;
        if (string.IsNullOrEmpty(authority) || authority.IndexOfAny(['/', '\\', '?', '#', '@', '%', ' ', '\t', ',', ';']) >= 0
            || !Uri.TryCreate($"{proto[0]}://{authority}/", UriKind.Absolute, out var origin)
            || !origin.IsWellFormedOriginalString()
            || origin.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || origin.Port == 0)
            return null;
        return new(origin);
    }

    /// <summary>Requires same-origin negotiation, mutations and WebSocket upgrades.</summary>
    public bool AllowsOrigin(HttpRequest request)
    {
        var supplied = request.Headers.Origin;
        var read = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        // Same-origin asset reads and SignalR polling GETs can legitimately omit Origin.
        var required = !read || request.HttpContext.WebSockets.IsWebSocketRequest;
        return !required && supplied.Count == 0
            || supplied.Count == 1 && supplied[0] == Origin.GetLeftPart(UriPartial.Authority);
    }
}
