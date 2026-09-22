using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Auth;

/// <summary>
/// The loopback bypass is decided by the address Fleet binds to, not the address a request arrives from.
/// A reverse proxy (tailscale serve, nginx) makes every outside caller arrive as loopback, so a Fleet
/// bound to 0.0.0.0 must refuse an un-tokened loopback request. A Fleet bound to 127.0.0.1 cannot be
/// reached that way and keeps the bypass — that is the desktop app's path.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class LoopbackAuthPolicyEndpointTests
{
    private const string ValidToken = "1234567890abcdef";

    // ── Bound to loopback: the bypass stays (desktop app) ─────────────────────

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public async Task Should_auto_authenticate_loopback_request_when_bound_to_loopback(string host)
    {
        await using var factory = CreateFactory(host);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_report_localhost_in_auth_status_when_bound_to_loopback()
    {
        await using var factory = CreateFactory("127.0.0.1");
        using var client = CreateClient(factory);

        var status = await GetAuthStatus(client);

        status.GetProperty("isLocalhost").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Should_sign_in_on_auth_login_when_bound_to_loopback()
    {
        await using var factory = CreateFactory("127.0.0.1");
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/auth/login");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders).ShouldBeTrue();
        setCookieHeaders.ShouldNotBeNull();
        setCookieHeaders.ShouldContain(header => header.Contains(".WeaveFleet.Auth=", StringComparison.Ordinal));
    }

    // ── Bound to a remote-reachable address: no bypass, proxy hole closed ─────

    [Theory]
    [InlineData("/api/user/me")]
    [InlineData("/api/sessions")]
    [InlineData("/api/workspaces")]
    [InlineData("/api/credentials")]
    public async Task Should_reject_untokened_loopback_request_when_bound_to_all_interfaces(string path)
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_reject_untokened_loopback_hub_negotiate_when_bound_to_all_interfaces()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);

        var response = await client.PostAsync("/hubs/session-events/negotiate?negotiateVersion=1", new StringContent(string.Empty));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.13")]
    public async Task Should_reject_untokened_loopback_request_for_every_remote_reachable_bind(string host)
    {
        await using var factory = CreateFactory(host);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Should_not_sign_in_on_auth_login_when_bound_to_all_interfaces()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/auth/login");

        var hasAuthCookie = response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders)
                            && setCookieHeaders.Any(header => header.Contains(".WeaveFleet.Auth=", StringComparison.Ordinal));

        hasAuthCookie.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_not_report_localhost_in_auth_status_when_bound_to_all_interfaces()
    {
        // The login page skips the token form and bounces through /auth/login when this is true;
        // reporting the raw remote IP would loop it forever against a Fleet bound to 0.0.0.0.
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);

        var status = await GetAuthStatus(client);

        status.GetProperty("isLocalhost").GetBoolean().ShouldBeFalse();
    }

    // ── The credentialed paths keep working on a remote-reachable bind ────────

    [Fact]
    public async Task Should_accept_bearer_token_from_loopback_when_bound_to_all_interfaces()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidToken);

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Should_accept_token_login_cookie_when_bound_to_all_interfaces()
    {
        // This is the printed /login?token=… flow — the one login a remotely-bound Fleet now asks for.
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);

        var loginResponse = await client.PostAsJsonAsync("/auth/token-login", new TokenLoginRequest(ValidToken));
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<JsonElement> GetAuthStatus(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/status");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static ApiWebApplicationFactory CreateFactory(string host)
        => new(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            host: host,
            configureTestServices: services =>
                services.AddSingleton<ILocalTokenAuthService>(new StubLocalTokenAuthService(ValidToken)));

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    private sealed record TokenLoginRequest(string Token);

    private sealed class StubLocalTokenAuthService(string token) : ILocalTokenAuthService
    {
        public string Token { get; } = token;

        public bool ValidateToken(string candidate)
            => string.Equals(candidate, Token, StringComparison.Ordinal);
    }
}
