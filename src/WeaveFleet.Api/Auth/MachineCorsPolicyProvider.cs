using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace WeaveFleet.Api.Auth;

/// <summary>
/// Lets a Fleet page on another machine call this one, as long as it brings the access token.
/// <para>
/// Another machine's page is another origin, and the configured policy only admits this Fleet's own page. A
/// request that presents the token authenticates by the token alone — no cookie, no loopback trust — so the
/// origin it comes from grants nothing, and any origin may read the answer. Everything else keeps the
/// configured policy: an un-tokened request from a foreign page gets no CORS headers, so the browser won't let
/// that page read what loopback auto-auth or a cookie would have returned.
/// </para>
/// <para>
/// A preflight carries no credential, only the list of headers the real request will send. It is answered for
/// any origin when that list includes <c>Authorization</c>; the real request then either presents a valid token
/// or gets a 401. Credentials (cookies) are never allowed cross-origin.
/// </para>
/// </summary>
public sealed class MachineCorsPolicyProvider(IOptions<CorsOptions> options) : ICorsPolicyProvider
{
    private static readonly CorsPolicy TokenPolicy = new CorsPolicyBuilder()
        .SetIsOriginAllowed(static _ => true)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .SetPreflightMaxAge(TimeSpan.FromMinutes(10))
        .Build();

    private readonly DefaultCorsPolicyProvider _configured = new(options);

    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (policyName is null && (BearerTokenHandler.PresentsToken(context.Request) || IsTokenPreflight(context.Request)))
            return Task.FromResult<CorsPolicy?>(TokenPolicy);

        return _configured.GetPolicyAsync(context, policyName);
    }

    private static bool IsTokenPreflight(HttpRequest request)
    {
        if (!HttpMethods.IsOptions(request.Method)
            || !request.Headers.ContainsKey(HeaderNames.AccessControlRequestMethod))
            return false;

        foreach (var value in request.Headers[HeaderNames.AccessControlRequestHeaders])
        {
            if (value is null)
                continue;

            foreach (var header in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(header, HeaderNames.Authorization, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
