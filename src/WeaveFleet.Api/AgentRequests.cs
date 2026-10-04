using System.Net;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Api;

/// <summary>
/// Calls from a harness process started with messages between sessions on. Its <c>FLEET_URL</c> is
/// <c>{fleet}/agent/{bridge token}</c>, so everything the agent sends through the Fleet API skill says which process
/// it came from, without the agent doing anything and without an <c>Authorization</c> header. The prefix is stripped
/// before routing, so the calls reach the same endpoints. The verified prefix is also the agent's credential:
/// <see cref="Auth.BearerTokenHandler"/> authenticates such a request (method <c>agent</c>) even when Fleet binds to a
/// remote-reachable address or requires the access token, where loopback auto-auth is off. Only this middleware sets
/// the marker, and only after checking the connection is loopback and the token belongs to a live process.
/// </summary>
public static class AgentRequests
{
    private static readonly object Key = new();

    /// <summary>
    /// The header an agent's call names its own session with: the harness's id for it. Fleet's plugins put it in the
    /// agent's shell as <c>FLEET_HARNESS_SESSION_ID</c>, and the Fleet API skill sends it when starting a session, so
    /// the new session knows which one started it.
    /// </summary>
    public const string HarnessSessionHeader = "X-Fleet-Harness-Session";

    /// <summary>Whether this request came from an agent process through its <c>/agent/{token}</c> prefix.</summary>
    public static bool IsAgentRequest(this HttpContext http) => http.Items.ContainsKey(Key);

    /// <summary>The bridge token of the agent process this request came from, or null when it isn't an agent's.</summary>
    public static string? AgentBridgeToken(this HttpContext http) => http.Items.TryGetValue(Key, out var token) ? token as string : null;

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

        context.Items[Key] = token;
        context.Request.PathBase = context.Request.PathBase.Add($"{SessionMessages.AgentPathPrefix}/{token}");
        context.Request.Path = end < 0 ? PathString.Empty : new PathString(value[end..]);
        await next();
    });

    private static bool IsLoopback(IPAddress? address)
        => address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
