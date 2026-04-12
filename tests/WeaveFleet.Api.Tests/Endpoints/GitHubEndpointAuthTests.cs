using System.Net;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Integration tests verifying that GitHub plugin endpoints require authentication
/// when auth is enabled, and allow authenticated access when credentials are provided.
/// </summary>
public sealed class GitHubEndpointAuthTests
{
    // ── Unauthenticated requests should be rejected ───────────────────────────

    [Theory]
    [InlineData("/api/integrations/github/auth/status")]
    [InlineData("/api/integrations/github/repos")]
    [InlineData("/api/integrations/github/bookmarks")]
    public async Task GitHubEndpoint_Returns401_WhenAuthEnabledAndUnauthenticated(string url)
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── Authenticated requests should succeed (not 401) ───────────────────────

    [Theory]
    [InlineData("/api/integrations/github/auth/status")]
    [InlineData("/api/integrations/github/bookmarks")]
    public async Task GitHubEndpoint_DoesNotReturn401_WhenAuthEnabledAndAuthenticated(string url)
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        // Should not be 401 — the request is authenticated.
        // Actual status depends on backend state (e.g. 200 for status, possibly 401 from GitHub proxy for repos).
        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    // ── Auth disabled: endpoints are accessible without credentials ────────────

    [Theory]
    [InlineData("/api/integrations/github/auth/status")]
    [InlineData("/api/integrations/github/bookmarks")]
    public async Task GitHubEndpoint_DoesNotReturn401_WhenAuthDisabled(string url)
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }
}
