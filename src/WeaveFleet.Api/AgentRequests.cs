using System.Net;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Api;

/// <summary>
/// Calls from a harness process started with messages between sessions on. Its <c>FLEET_URL</c> is
/// <c>{fleet}/agent/{bridge token}</c>, so everything the agent sends through the Fleet API skill says which process
/// it came from, without the agent doing anything and without an <c>Authorization</c> header that would change how
/// Fleet authenticates it. The prefix is stripped before routing, so the calls reach the same endpoints.
/// </summary>
public static class AgentRequests
{
    private static readonly object Key = new();

    /// <summary>Whether this request came from an agent process through its <c>/agent/{token}</c> prefix.</summary>
    public static bool IsAgentRequest(this HttpContext http) => http.Items.ContainsKey(Key);

    /// <summary>Recognizes and strips the prefix. Must run before routing.</summary>
    public static IApplicationBuilder UseAgentRequests(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments(SessionMessages.AgentPathPrefix, out var rest))
        {
            await next();
            return;
        }

        var value = rest.Value ?? string.Empty;
        var end = value.IndexOf('/', 1);
        var token = end < 0 ? value.TrimStart('/') : value[1..end];
        // Each harness knows its own processes' tokens.
        var tokens = context.RequestServices.GetServices<IHarnessBridgeTokens>();

        // Only a live process on this machine; anything else looks like a path that doesn't exist.
        if (!IsLoopback(context.Connection.RemoteIpAddress) || !tokens.Any(t => t.IsKnown(token)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Items[Key] = true;
        context.Request.PathBase = context.Request.PathBase.Add($"{SessionMessages.AgentPathPrefix}/{token}");
        context.Request.Path = end < 0 ? PathString.Empty : new PathString(value[end..]);
        await next();
    });

    private static bool IsLoopback(IPAddress? address)
        => address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
