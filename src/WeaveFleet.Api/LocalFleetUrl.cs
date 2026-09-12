using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api;

/// <summary>
/// Fleet's own URL for local callers. A wildcard or <c>localhost</c> host becomes <c>127.0.0.1</c>, so the
/// caller connects over loopback. The port comes from <see cref="FleetOptions.Port"/>, which is known before the
/// server starts (pool warmup runs first). Only when that's 0 does it come from the address Kestrel bound.
/// </summary>
internal sealed class LocalFleetUrl(FleetOptions options, IServer server) : ILocalFleetUrl
{
    public string? TryGet()
    {
        var port = options.Port;
        if (port == 0)
        {
            var bound = server.Features.Get<IServerAddressesFeature>()?.Addresses
                .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
                .FirstOrDefault(uri => uri is { Port: > 0 });
            if (bound is null)
                return null;
            port = bound.Port;
        }

        return $"http://{LoopbackHost(options.Host)}:{port}";
    }

    internal static string LoopbackHost(string host)
    {
        var trimmed = host.Trim().Trim('[', ']');
        if (trimmed is "" or "*" or "+" or "localhost")
            return "127.0.0.1";

        if (!IPAddress.TryParse(trimmed, out var address))
            return trimmed;

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return "127.0.0.1";

        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{trimmed}]" : trimmed;
    }
}
