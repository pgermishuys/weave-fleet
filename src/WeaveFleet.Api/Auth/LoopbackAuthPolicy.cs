using System.Net;
using Microsoft.Extensions.Options;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Auth;

/// <summary>
/// Decides whether a loopback request may be authenticated without a credential.
/// <para>
/// The policy is derived from the address Fleet <em>binds</em> to, never from the address a request
/// <em>arrives</em> from. A reverse proxy (<c>tailscale serve</c>, nginx, …) terminates the outside
/// connection and opens a fresh one to <c>127.0.0.1</c>, so every remote caller would otherwise look
/// like loopback and be let in unauthenticated. The bind address cannot be influenced by a caller, and
/// <c>X-Forwarded-*</c> headers deliberately play no part here.
/// </para>
/// <list type="bullet">
/// <item>Bound to loopback only — nothing outside the machine can connect, so loopback auto-auth is safe.</item>
/// <item>Bound to anything else (<c>0.0.0.0</c>, <c>::</c>, a LAN/tailnet IP) — a credential is always required.</item>
/// <item><c>Fleet:Auth:RequireToken</c> (<c>--require-token</c>) — a credential is always required, whatever the bind.
/// This is the setting for a loopback-bound Fleet behind <c>tailscale serve</c> or another reverse proxy.</item>
/// </list>
/// <para>
/// A loopback request that carries a forwarding header (<c>X-Forwarded-For</c>, <c>Forwarded</c>, Tailscale's
/// identity headers, …) came through a proxy, so it never gets auto-auth either. Headers can only take the bypass
/// away, never grant it, so a caller gains nothing by sending or omitting them.
/// </para>
/// </summary>
public sealed class LoopbackAuthPolicy
{
    private static readonly string[] ProxyHeaders =
    [
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "Forwarded",
        "X-Real-IP",
        "Tailscale-User-Login",
        "Tailscale-User-Name",
    ];

    public LoopbackAuthPolicy(IOptions<FleetOptions> options)
        : this(options.Value.Host, options.Value.Auth.RequireToken, options.Value.Port)
    {
    }

    public LoopbackAuthPolicy(string? host, bool requireToken = false, int port = 0, ReachListeners? reach = null)
    {
        BoundHost = host ?? string.Empty;
        IsBoundWide = IsRemoteReachableHost(host);
        RequiresToken = requireToken;
        PrimaryPort = port;
        Reach = reach;
    }

    /// <summary>The configured bind address the policy was derived from.</summary>
    public string BoundHost { get; }

    /// <summary>The port of the loopback listener, or 0 when the policy doesn't tell listeners apart by port.</summary>
    public int PrimaryPort { get; }

    /// <summary>The extra listeners the desktop app opens when others may reach it; null when this Fleet has none.</summary>
    public ReachListeners? Reach { get; }

    /// <summary>True when the configured bind address itself lets machines other than this one connect.</summary>
    public bool IsBoundWide { get; }

    /// <summary>True when machines other than this one can connect: a wide bind, or an extra listener for them.</summary>
    public bool IsRemoteReachable => IsBoundWide || (Reach?.HasListeners ?? false);

    /// <summary>True when the configuration asks for a credential on every request, loopback ones included.</summary>
    public bool RequiresToken { get; }

    /// <summary>True when a loopback request may be authenticated without presenting a credential.</summary>
    public bool AllowsLoopbackAutoAuth => !IsBoundWide && !RequiresToken;

    /// <summary>
    /// True when this request should be authenticated without a credential: it arrived over loopback, not through
    /// a proxy, on the loopback listener, <em>and</em> Fleet is bound so that only this machine could have sent it.
    /// </summary>
    public bool GrantsAutoAuth(HttpContext context)
        => AllowsLoopbackAutoAuth && IsLoopbackRequest(context) && !CameThroughProxy(context.Request) && ArrivedOnLoopbackListener(context);

    /// <summary>
    /// True when the connection came in on the loopback listener: this machine's loopback address, on Fleet's own port.
    /// The listeners for other devices (and the second loopback port for a tunnel) fail it, so a request on them needs a
    /// key whatever its remote address or headers claim. A connection without a local endpoint (an in-memory test
    /// server) counts only while no extra listener is open.
    /// </summary>
    public bool ArrivedOnLoopbackListener(HttpContext context)
    {
        var local = context.Connection.LocalIpAddress;
        if (local is null)
            return !(Reach?.HasListeners ?? false);

        if (!IPAddress.IsLoopback(local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local))
            return false;

        return PrimaryPort == 0 || context.Connection.LocalPort == PrimaryPort;
    }

    /// <summary>True when the request carries a header a reverse proxy adds.</summary>
    public static bool CameThroughProxy(HttpRequest request)
    {
        foreach (var header in ProxyHeaders)
        {
            if (request.Headers.ContainsKey(header))
                return true;
        }

        return false;
    }

    /// <summary>True when the connection's remote address is a loopback address.</summary>
    public static bool IsLoopbackRequest(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null)
            return false;

        return IPAddress.IsLoopback(remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp);
    }

    /// <summary>
    /// True when a host Kestrel binds to is reachable from another machine. Mirrors t3code's
    /// <c>isRemoteReachableHost</c>.
    /// </summary>
    public static bool IsRemoteReachableHost(string? host)
    {
        if (host is null)
            return false;

        var trimmed = host.Trim();

        // Wildcard binds listen on every interface.
        if (trimmed is "0.0.0.0" or "::" or "[::]" or "*" or "+")
            return true;

        // An unset host means Kestrel's own default, which is loopback.
        if (trimmed.Length == 0)
            return false;

        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        if (trimmed is "::1" or "[::1]")
            return false;

        if (IPAddress.TryParse(trimmed.Trim('[', ']'), out var address))
            return !IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

        // Any other name resolves to an interface that is not necessarily loopback — assume reachable.
        return true;
    }
}
