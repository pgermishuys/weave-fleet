using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the OIDC browser flow: sign-in via the IdP and the returnUrl deep-link
/// round-trip. CSRF, sign-out and <c>/api/user/me</c> are covered in WeaveFleet.Api.Tests.
/// </summary>
#pragma warning disable CA1001 // Types that own disposable fields should implement IDisposable — disposal handled by IAsyncLifetime
[Trait("Category", "E2E")]
[Trait("Category", "AuthE2E")]
public sealed class OidcSignInTests : AuthE2ETestBase,
    IClassFixture<AuthFleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
#pragma warning restore CA1001
{
    public OidcSignInTests(AuthFleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Unauthenticated user navigates to "/" → redirected to IdP login → enters credentials
    /// → redirected back to Fleet → dashboard (or SPA root) is visible.
    /// </summary>
    [Fact]
    public async Task SignIn_FromRoot_RedirectsToIdpAndBackToDashboard()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("testuser", "password", "/");

            // After login the browser should be back on the Fleet SPA
            var url = Page.Url;
            url.ShouldStartWith(ServerUrl);

            // Verify the auth cookie was set and /api/user/me returns 200
            await AssertAuthenticatedAsync();
        });
    }

    /// <summary>
    /// Task 10: navigating to <c>/auth/login?returnUrl=/sessions/test-deep-link</c> flows
    /// through the OIDC challenge and, after sign-in, the browser lands on the deep-linked
    /// SPA route.
    /// </summary>
    [Fact]
    public async Task ReturnUrl_DeepLink_LandsOnTargetRouteAfterSignIn()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("/auth/login?returnUrl=%2Fsessions%2Ftest-deep-link");

            // After sign-in the browser should land on the deep-linked route
            var url = Page.Url;
            url.ShouldContain("/sessions/test-deep-link");
        });
    }
}
