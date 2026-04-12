using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// E2E tests verifying that GitHub integration endpoints require authentication
/// when auth is enabled. Validates that unauthenticated API requests to GitHub
/// plugin endpoints are rejected, and authenticated requests succeed.
/// </summary>
#pragma warning disable CA1001 // Types that own disposable fields should implement IDisposable — disposal handled by IAsyncLifetime
[Trait("Category", "E2E")]
[Trait("Category", "AuthE2E")]
public sealed class GitHubEndpointSecurityTests : AuthE2ETestBase,
    IClassFixture<AuthFleetWebApplicationFactory>,
    IClassFixture<PlaywrightFixture>
#pragma warning restore CA1001
{
    public GitHubEndpointSecurityTests(AuthFleetWebApplicationFactory factory, PlaywrightFixture playwright)
        : base(factory, playwright) { }

    /// <summary>
    /// Unauthenticated GET to /api/integrations/github/auth/status returns 401
    /// when auth is enabled.
    /// </summary>
    [Fact]
    public async Task GitHubAuthStatus_Unauthenticated_Returns401()
    {
        await WithFailureCapture(async () =>
        {
            // Do NOT log in — make a raw API request without any session cookie
            var response = await Page.APIRequest.GetAsync($"{ServerUrl}/api/integrations/github/auth/status");

            // Should be 401 (API request won't follow OIDC redirects)
            response.Status.ShouldBe(401);
        });
    }

    /// <summary>
    /// Unauthenticated GET to /api/integrations/github/repos returns 401
    /// when auth is enabled.
    /// </summary>
    [Fact]
    public async Task GitHubRepos_Unauthenticated_Returns401()
    {
        await WithFailureCapture(async () =>
        {
            var response = await Page.APIRequest.GetAsync($"{ServerUrl}/api/integrations/github/repos");

            response.Status.ShouldBe(401);
        });
    }

    /// <summary>
    /// After sign-in, GET to /api/integrations/github/auth/status returns 200
    /// (the endpoint is accessible to authenticated users).
    /// </summary>
    [Fact]
    public async Task GitHubAuthStatus_Authenticated_Returns200()
    {
        await WithFailureCapture(async () =>
        {
            await LoginAsync("testuser", "password");
            await AssertAuthenticatedAsync();

            var response = await Page.APIRequest.GetAsync($"{ServerUrl}/api/integrations/github/auth/status");

            // Authenticated request should be allowed through (200 with connected=false since no token stored)
            response.Status.ShouldBe(200);
        });
    }
}
