using Microsoft.AspNetCore.Http;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// User context for authenticated (cloud) mode.
/// Extracts identity from <see cref="HttpContext.User"/> claims via <see cref="IHttpContextAccessor"/>.
/// Maps OIDC claims: <c>sub</c> → <see cref="UserId"/>, <c>email</c> → <see cref="Email"/>,
/// <c>name</c> → <see cref="DisplayName"/>. Registered when <c>Auth.Enabled = true</c>.
/// </summary>
public sealed class ClaimsUserContext : IUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ClaimsUserContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string UserId
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(BackgroundUserContext.UserId))
                return BackgroundUserContext.UserId;

            var sub = _httpContextAccessor.HttpContext?.User.FindFirst("sub")?.Value
                   ?? _httpContextAccessor.HttpContext?.User.FindFirst(
                       System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrWhiteSpace(sub))
                throw new InvalidOperationException(
                    "Cannot resolve UserId: the 'sub' claim is missing from the current user principal. " +
                    "Ensure the authentication middleware has run and the request is authenticated.");

            return sub;
        }
    }

    public string? Email =>
        _httpContextAccessor.HttpContext is null && !string.IsNullOrWhiteSpace(BackgroundUserContext.UserId)
            ? null
            :
        _httpContextAccessor.HttpContext?.User.FindFirst("email")?.Value
        ?? _httpContextAccessor.HttpContext?.User.FindFirst(
            System.Security.Claims.ClaimTypes.Email)?.Value;

    public string? DisplayName =>
        !string.IsNullOrWhiteSpace(BackgroundUserContext.UserId)
            ? BackgroundUserContext.UserId
            :
        _httpContextAccessor.HttpContext?.User.FindFirst("name")?.Value
        ?? _httpContextAccessor.HttpContext?.User.FindFirst(
            System.Security.Claims.ClaimTypes.Name)?.Value;

    public bool IsAuthenticated =>
        !string.IsNullOrWhiteSpace(BackgroundUserContext.UserId)
        || (_httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated ?? false);
}
