using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Machines;

namespace WeaveFleet.Api;

/// <summary>
/// With <see cref="FleetOptions.PrivateNetworksOnly"/> on, a request that arrived on a public address of this machine is
/// dropped before anything reads it: no answer, not even a status, so nothing out there learns Fleet is here. Loopback,
/// the private ranges, link-local and Tailscale's stay. It goes by the address the request came in on, not the
/// caller's, so a Fleet bound to 0.0.0.0 behaves as if it only listened on those.
/// </summary>
internal static class PrivateNetworksOnly
{
    public static IApplicationBuilder UsePrivateNetworksOnly(this IApplicationBuilder app, FleetOptions options)
    {
        if (!options.PrivateNetworksOnly)
            return app;

        return app.Use(FilterAsync);
    }

    /// <summary>Answers the request through <paramref name="next"/>, or drops it when it came in on a public address.</summary>
    internal static Task FilterAsync(HttpContext context, Func<Task> next)
    {
        if (Allows(context.Connection.LocalIpAddress))
            return next();
        context.Abort();
        return Task.CompletedTask;
    }

    /// <summary>Whether a request that came in on <paramref name="local"/> is answered. Null (an in-memory test server) is.</summary>
    internal static bool Allows(System.Net.IPAddress? local) => local is null || MachineAddresses.IsPrivate(local);
}
