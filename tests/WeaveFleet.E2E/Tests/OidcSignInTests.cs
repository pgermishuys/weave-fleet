using System.Text.Json;
using Microsoft.Playwright;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests for the OIDC authentication pipeline: sign-in, sign-out,
/// CSRF token lifecycle, and returnUrl deep-link round-trip.
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
    /// After sign-in, <c>GET /api/user/me</c> returns the authenticated user's identity
    /// with correct userId, email, and displayName fields.
    /// </summary>
    [Fact]
    public async Task UserMe_AfterSignIn_ReturnsUserIdentity()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("testuser", "password");

            var response = await Page.APIRequest.GetAsync($"{ServerUrl}/api/user/me");
            response.Status.ShouldBe(200);

            var body = await response.JsonAsync();
            body.ShouldNotBeNull();

            var userId = body.Value.GetProperty("userId").GetString();
            var email = body.Value.GetProperty("email").GetString();
            var displayName = body.Value.GetProperty("displayName").GetString();

            userId.ShouldNotBeNullOrWhiteSpace();
            email.ShouldBe("test@example.com");
            displayName.ShouldNotBeNullOrWhiteSpace();
        });
    }

    /// <summary>
    /// After sign-in, the <c>.WeaveFleet.CSRF</c> cookie is set on a GET request,
    /// and a POST to <c>/api/user/me/complete-onboarding</c> without the
    /// <c>X-CSRF-Token</c> header returns 400.
    /// </summary>
    [Fact]
    public async Task CsrfLifecycle_PostWithoutToken_Returns400()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("testuser", "password");

            // Trigger CSRF cookie emission via a GET to an API endpoint
            var getResponse = await Page.APIRequest.GetAsync($"{ServerUrl}/api/user/me");
            getResponse.Status.ShouldBe(200);

            // Verify the CSRF cookie was set
            var cookies = await Page.Context.CookiesAsync([ServerUrl]);
            var csrfCookie = cookies.FirstOrDefault(c =>
                c.Name.Equals(".WeaveFleet.CSRF", StringComparison.OrdinalIgnoreCase));
            csrfCookie.ShouldNotBeNull("Expected .WeaveFleet.CSRF cookie to be set after GET");

            // POST without X-CSRF-Token header should be rejected
            var postResponse = await Page.APIRequest.PostAsync(
                $"{ServerUrl}/api/user/me/complete-onboarding",
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>()
                });

            postResponse.Status.ShouldBe(400);
        });
    }

    /// <summary>
    /// Sign out via <c>POST /auth/logout</c> with CSRF token → auth cookie is cleared →
    /// subsequent <c>GET /api/user/me</c> returns 401.
    /// </summary>
    [Fact]
    public async Task SignOut_ClearsAuthCookie_SubsequentApiReturns401()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("testuser", "password");
            await AssertAuthenticatedAsync();

            // Ensure CSRF cookie is available (trigger with a GET)
            await Page.APIRequest.GetAsync($"{ServerUrl}/api/user/me");

            await LogoutAsync();

            // After logout, /api/user/me should return 401
            var response = await Page.APIRequest.GetAsync($"{ServerUrl}/api/user/me");
            response.Status.ShouldBe(401);
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
