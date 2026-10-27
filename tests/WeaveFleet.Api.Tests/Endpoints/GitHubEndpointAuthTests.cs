using System.Net;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Integration tests verifying that GitHub plugin endpoints require authentication
/// when auth is enabled, and allow authenticated access when credentials are provided.
/// </summary>
public sealed class GitHubEndpointAuthTests :
    IClassFixture<GitHubEndpointAuthTests.SignInRequiredServer>,
    IClassFixture<GitHubEndpointAuthTests.SignedInServer>,
    IClassFixture<GitHubEndpointAuthTests.AuthDisabledServer>
{
    private readonly ApiWebApplicationFactory _signInRequired;
    private readonly ApiWebApplicationFactory _signedIn;
    private readonly ApiWebApplicationFactory _authDisabled;

    public GitHubEndpointAuthTests(SignInRequiredServer signInRequired, SignedInServer signedIn, AuthDisabledServer authDisabled)
    {
        _signInRequired = signInRequired.Factory;
        _signedIn = signedIn.Factory;
        _authDisabled = authDisabled.Factory;
    }

    // ── Unauthenticated requests should be rejected ───────────────────────────

    [Theory]
    [InlineData("/api/integrations/github/auth/status")]
    [InlineData("/api/integrations/github/repos")]
    [InlineData("/api/integrations/github/bookmarks")]
    public async Task GitHubEndpoint_Returns401_WhenAuthEnabledAndUnauthenticated(string url)
    {
        var factory = _signInRequired;
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
        var factory = _signedIn;
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
        var factory = _authDisabled;
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    public sealed class SignInRequiredServer : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = new(authEnabled: true, useTestAuthentication: false);

        public void Dispose() => Factory.Dispose();
    }

    public sealed class SignedInServer : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = new(authEnabled: true, useTestAuthentication: true);

        public void Dispose() => Factory.Dispose();
    }

    public sealed class AuthDisabledServer : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = new(authEnabled: false);

        public void Dispose() => Factory.Dispose();
    }
}
