using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Auth;

[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class TokenLoginEndpointTests
{
    private const string ValidToken = "1234567890abcdef";

    [Fact]
    public async Task Should_return_ok_and_set_cookie_when_token_login_token_is_valid()
    {
        await using var factory = CreateFactory();
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
    public async Task Should_return_unauthorized_when_token_login_token_is_invalid()
    {
        await using var factory = CreateFactory();
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/auth/token-login", new TokenLoginRequest("invalid-token"));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_return_ok_when_bearer_token_is_valid()
    {
        await using var factory = CreateFactory();
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_return_unauthorized_when_bearer_token_is_invalid()
    {
        await using var factory = CreateFactory();
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_clear_auth_cookie_and_reject_protected_requests_after_logout()
    {
        await using var factory = CreateFactory();
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

    private static ApiWebApplicationFactory CreateFactory()
        => new(
            authEnabled: false,
            tokenAuthEnabled: true,
            configureTestServices: services =>
                services.AddSingleton<ILocalTokenAuthService>(new TestLocalTokenAuthService(ValidToken)));

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
