using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
        app.MapGet("/api/auth/status", (HttpContext httpContext) =>
        {
            var authenticated = httpContext.User.Identity?.IsAuthenticated ?? false;

            return Results.Ok(new AuthStatusResponse(
                fleetOptions.Auth.Enabled,
                fleetOptions.Auth.TokenAuthEnabled,
                authenticated));
        })
        .AllowAnonymous()
        .WithName("GetAuthStatus");

        app.MapGet("/auth/login", (HttpContext httpContext, string? returnUrl) =>
        {
            var redirectUri = NormalizeReturnUrl(httpContext, returnUrl);

            if (!fleetOptions.Auth.Enabled)
                return Results.LocalRedirect(redirectUri);

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = redirectUri },
                [OpenIdConnectDefaults.AuthenticationScheme]);
        })
        .AllowAnonymous()
        .WithName("Login");

        if (!fleetOptions.Auth.Enabled && fleetOptions.Auth.TokenAuthEnabled)
        {
            app.MapPost("/auth/token-login", async Task<IResult> (
                HttpContext httpContext,
                TokenLoginRequest request,
                ILocalTokenAuthService localTokenAuthService) =>
            {
                if (string.IsNullOrWhiteSpace(request.Token) || !localTokenAuthService.ValidateToken(request.Token))
                    return Results.Unauthorized();

                var claims = new[]
                {
                    new Claim(ClaimTypes.Name, "local"),
                    new Claim(ClaimTypes.NameIdentifier, "local"),
                    new Claim("sub", "local")
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                return Results.Ok();
            })
            .AllowAnonymous()
            .WithName("TokenLogin");
        }

        app.MapPost("/auth/logout", async (HttpContext httpContext, string? returnUrl) =>
        {
            var redirectUri = NormalizeReturnUrl(httpContext, returnUrl);

            if (!fleetOptions.Auth.Enabled)
            {
                await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.LocalRedirect(redirectUri);
            }

            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = redirectUri },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        })
        .RequireAuthorization("FleetUser")
        .WithName("Logout");

        return app;
    }

    private static string NormalizeReturnUrl(HttpContext httpContext, string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return "/";

        return IsLocalUrl(returnUrl)
            ? returnUrl
            : "/";
    }

    private static bool IsLocalUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        if (url[0] == '/')
        {
            if (url.Length == 1)
                return true;

            return url[1] != '/' && url[1] != '\\';
        }

        if (url[0] == '~' && url.Length > 1 && url[1] == '/')
            return true;

        return false;
    }

}

internal sealed record AuthStatusResponse(
    bool AuthEnabled,
    bool TokenAuthEnabled,
    bool Authenticated);

internal sealed record TokenLoginRequest(string Token);
#pragma warning restore IL2026
