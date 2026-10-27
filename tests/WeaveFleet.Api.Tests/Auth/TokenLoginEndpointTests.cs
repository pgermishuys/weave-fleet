using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Tests.Auth;

public sealed class TokenLoginEndpointTests :
    IClassFixture<TokenLoginEndpointTests.Server>,
    IClassFixture<TokenLoginEndpointTests.LocalhostServer>
{
    private const string ValidToken = "1234567890abcdef";

    private readonly ApiWebApplicationFactory _server;
    private readonly ApiWebApplicationFactory _localhost;

    public TokenLoginEndpointTests(Server server, LocalhostServer localhost)
    {
        _server = server.Factory;
        _localhost = localhost.Factory;
    }

    [Fact]
    public async Task Should_return_ok_and_set_cookie_when_token_login_token_is_valid()
    {
        var factory = _server;
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/auth/token-login", new TokenLoginRequest(ValidToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders).ShouldBeTrue();
        setCookieHeaders.ShouldNotBeNull();
        setCookieHeaders.ShouldContain(header => header.Contains(".WeaveFleet.Auth=", StringComparison.Ordinal));

        var meResponse = await client.GetAsync("/api/user/me");

        meResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_keep_the_token_sign_in_past_the_browser_session_for_30_days()
    {
        var factory = _server;
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/auth/token-login", new TokenLoginRequest(ValidToken));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders).ShouldBeTrue();
        var authCookie = setCookieHeaders!.Single(header => header.StartsWith(".WeaveFleet.Auth=", StringComparison.Ordinal));

        // Without an expiry the browser drops the cookie when it closes, and the token is asked for again.
        var expires = authCookie.Split(';')
            .Select(attribute => attribute.Trim())
            .Single(attribute => attribute.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        var expiresAt = DateTimeOffset.Parse(expires["expires=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        (expiresAt - DateTimeOffset.UtcNow).ShouldBeGreaterThan(TimeSpan.FromDays(29));
    }

    [Fact]
    public async Task Should_return_unauthorized_when_token_login_token_is_invalid()
    {
        var factory = _server;
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/auth/token-login", new TokenLoginRequest("invalid-token"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_ok_when_bearer_token_is_valid()
    {
        var factory = _server;
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_return_unauthorized_when_bearer_token_is_invalid()
    {
        var factory = _server;
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_ok_when_request_is_from_localhost_without_bearer_token()
    {
        var factory = _localhost;
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_clear_auth_cookie_and_reject_protected_requests_after_logout()
    {
        var factory = _server;
        using var client = CreateClient(factory);

        var loginResponse = await client.PostAsJsonAsync("/auth/token-login", new TokenLoginRequest(ValidToken));
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var authenticatedResponse = await client.GetAsync("/api/user/me");
        authenticatedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var logoutResponse = await client.PostAsync("/auth/logout", new StringContent(string.Empty));

        logoutResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        logoutResponse.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders).ShouldBeTrue();
        setCookieHeaders.ShouldNotBeNull();
        setCookieHeaders.ShouldContain(header =>
            header.Contains(".WeaveFleet.Auth=", StringComparison.Ordinal)
            && (header.Contains("max-age=0", StringComparison.OrdinalIgnoreCase)
                || header.Contains("expires=", StringComparison.OrdinalIgnoreCase)));

        var postLogoutResponse = await client.GetAsync("/api/user/me");

        postLogoutResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static ApiWebApplicationFactory CreateFactory(bool simulateLocalhostRequest = false)
        => new(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: simulateLocalhostRequest,
            configureTestServices: services =>
                services.AddSingleton<ILocalTokenAuthService>(new TestLocalTokenAuthService(ValidToken)));

    public sealed class Server : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = CreateFactory();

        public void Dispose() => Factory.Dispose();
    }

    public sealed class LocalhostServer : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = CreateFactory(simulateLocalhostRequest: true);

        public void Dispose() => Factory.Dispose();
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    private sealed record TokenLoginRequest(string Token);

    private sealed class TestLocalTokenAuthService : ILocalTokenAuthService
    {
        public TestLocalTokenAuthService(string token)
        {
            Token = token;
        }

        public string Token { get; }

        public bool ValidateToken(string candidate)
            => string.Equals(candidate, Token, StringComparison.Ordinal);
    }
}
