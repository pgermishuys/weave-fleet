using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Auth;

/// <summary>
/// Authenticates process-local bearer tokens without interfering with other schemes.
/// </summary>
public sealed class BearerTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ILocalTokenAuthService localTokenAuthService,
    DeviceTokenService deviceTokens,
    LoopbackAuthPolicy loopbackAuthPolicy)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalBearer";

    /// <summary>
    /// The claim that says how a request proved who it is: <see cref="TokenMethod"/> when it presented the access
    /// token, <see cref="LoopbackMethod"/> when loopback auto-auth let it in, <see cref="AgentMethod"/> when it came
    /// from an agent process under its verified <c>/agent/{token}</c> prefix. Checks that must not be satisfied by
    /// ambient trust (a page on another origin opening a terminal) ask for the token method, which agents don't get.
    /// </summary>
    public const string MethodClaim = "amr";
    public const string TokenMethod = "token";
    public const string LoopbackMethod = "loopback";
    public const string AgentMethod = "agent";

    /// <summary>The query parameter a browser WebSocket or EventSource carries the token in, since neither can set headers.</summary>
    public const string AccessTokenQueryParameter = "access_token";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = FindPresentedToken(Request);

        // No credential at all: auto-authenticate only when the bind address makes loopback trustworthy.
        if (presented is null)
        {
            if (GrantsAutoAuth())
                return CreateSuccessResult(LoopbackMethod);

            // An agent process under /agent/{token}: the middleware already checked the connection is loopback and
            // the token belongs to a process Fleet started. That per-process secret is the credential, whatever the
            // bind address. A request with proxy headers didn't come straight from such a process, so it gets nothing.
            return Context.IsAgentRequest() && !LoopbackAuthPolicy.CameThroughProxy(Request)
                ? CreateSuccessResult(AgentMethod)
                : AuthenticateResult.NoResult();
        }

        // A credential was presented, so it has to be right. A wrong one never falls back to auto-auth.
        if (presented.Length == 0)
            return AuthenticateResult.NoResult();

        // A paired device's own token. Anything else is checked as the machine token.
        if (DeviceToken.HasPrefix(presented))
        {
            var device = await deviceTokens.ValidateAsync(presented);
            return device is null
                ? AuthenticateResult.NoResult()
                : CreateSuccessResult(TokenMethod, FleetClaims.ForDevice(device.DeviceId));
        }

        if (!localTokenAuthService.ValidateToken(presented))
            return AuthenticateResult.NoResult();

        return CreateSuccessResult(TokenMethod, [new Claim(FleetClaims.Scope, FleetClaims.Owner)]);
    }

    /// <summary>True when the request carries a bearer credential, in the header or where a socket may carry it.</summary>
    public static bool PresentsToken(HttpRequest request) => FindPresentedToken(request) is { Length: > 0 };

    /// <summary>True when the request was let in by the access token rather than by a cookie or loopback.</summary>
    public static bool AuthenticatedWithToken(ClaimsPrincipal user) => user.HasClaim(MethodClaim, TokenMethod);

    /// <summary>
    /// The credential the request presents: the <c>Authorization: Bearer</c> value, or the <c>access_token</c> query
    /// parameter on a hub request or a WebSocket upgrade. Null when it presents none; empty when it presents one
    /// that can't be a token. The query form is limited to those two, so a token never needs to sit in an ordinary
    /// URL where it would reach logs and history.
    /// </summary>
    private static string? FindPresentedToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderNames.Authorization, out var authorizationHeaderValues))
        {
            var authorizationHeader = authorizationHeaderValues.ToString();
            if (!string.IsNullOrWhiteSpace(authorizationHeader))
            {
                if (!AuthenticationHeaderValue.TryParse(authorizationHeader, out var headerValue)
                    || !string.Equals(headerValue.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                    return string.Empty;

                return headerValue.Parameter?.Trim() ?? string.Empty;
            }
        }

        if (!AcceptsQueryToken(request))
            return null;

        if (!request.Query.TryGetValue(AccessTokenQueryParameter, out var queryValues))
            return null;

        return queryValues.ToString().Trim();
    }

    private static bool AcceptsQueryToken(HttpRequest request)
        => request.Path.StartsWithSegments("/hubs") || IsWebSocketUpgrade(request);

    /// <summary>
    /// Read from the headers, not <c>HttpContext.WebSockets</c>: authentication runs before <c>UseWebSockets</c>,
    /// so the WebSocket feature doesn't know about the upgrade yet when the token has to be found.
    /// </summary>
    private static bool IsWebSocketUpgrade(HttpRequest request)
    {
        if (request.HttpContext.WebSockets.IsWebSocketRequest)
            return true;

        foreach (var value in request.Headers.Upgrade)
        {
            if (value is not null && value.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool GrantsAutoAuth() => loopbackAuthPolicy.GrantsAutoAuth(Context);

    private AuthenticateResult CreateSuccessResult(string method, IEnumerable<Claim>? scope = null)
    {
        List<Claim> claims =
        [
            new Claim(ClaimTypes.Name, "local"),
            new Claim(ClaimTypes.NameIdentifier, "local"),
            new Claim("sub", "local"),
            new Claim(MethodClaim, method),
            .. scope ?? [],
        ];

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
