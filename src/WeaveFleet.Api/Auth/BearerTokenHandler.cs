using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Auth;

/// <summary>
/// Authenticates process-local bearer tokens without interfering with other schemes.
/// </summary>
public sealed class BearerTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ILocalTokenAuthService localTokenAuthService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check if request has Authorization header
        var hasAuthorizationHeader = Request.Headers.TryGetValue(HeaderNames.Authorization, out var authorizationHeaderValues);
        
        // If no Authorization header and request is from loopback, auto-authenticate
        if (!hasAuthorizationHeader)
        {
            if (IsLocalhostRequest())
            {
                return Task.FromResult(CreateSuccessResult());
            }
            
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var authorizationHeader = authorizationHeaderValues.ToString();
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            if (IsLocalhostRequest())
            {
                return Task.FromResult(CreateSuccessResult());
            }
            
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!AuthenticationHeaderValue.TryParse(authorizationHeader, out var headerValue))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!string.Equals(headerValue.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Validate the token
        if (!localTokenAuthService.ValidateToken(headerValue.Parameter))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(CreateSuccessResult());
    }

    private bool IsLocalhostRequest()
    {
        var remoteIp = Context.Connection.RemoteIpAddress;
        return remoteIp is not null && IPAddress.IsLoopback(remoteIp);
    }

    private AuthenticateResult CreateSuccessResult()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "local"),
            new Claim(ClaimTypes.NameIdentifier, "local"),
            new Claim("sub", "local")
        };

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
