using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, FleetOptions fleetOptions)
    {
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

        app.MapPost("/auth/logout", (HttpContext httpContext, string? returnUrl) =>
        {
            var redirectUri = NormalizeReturnUrl(httpContext, returnUrl);

            if (!fleetOptions.Auth.Enabled)
                return Results.LocalRedirect(redirectUri);

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
#pragma warning restore IL2026
