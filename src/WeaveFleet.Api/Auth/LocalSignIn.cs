using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace WeaveFleet.Api.Auth;

/// <summary>
/// The sign-in cookie local mode hands out after a token checks out. Persistent, so the cookie outlives the browser
/// session: a token is pasted (or a phone paired) once per browser, not on every visit. Sliding expiry renews it
/// while Fleet is in use.
/// <para>
/// The cookie never carries <c>amr=token</c>: a cookie arrives with any request the browser makes, so it mustn't
/// pass the checks that only a presented token may pass (a terminal opened from another origin).
/// </para>
/// </summary>
public static class LocalSignIn
{
    /// <summary>Signs in the machine's owner.</summary>
    public static Task OwnerAsync(HttpContext httpContext) =>
        SignInAsync(httpContext, [new Claim(FleetClaims.Scope, FleetClaims.Owner)]);

    /// <summary>Signs in a paired device. The cookie stops working when the device is removed or expires.</summary>
    public static Task DeviceAsync(HttpContext httpContext, string deviceId) =>
        SignInAsync(httpContext, FleetClaims.ForDevice(deviceId));

    private static Task SignInAsync(HttpContext httpContext, IEnumerable<Claim> scope)
    {
        List<Claim> claims =
        [
            new Claim(ClaimTypes.Name, "local"),
            new Claim(ClaimTypes.NameIdentifier, "local"),
            new Claim("sub", "local"),
            .. scope,
        ];

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }
}
