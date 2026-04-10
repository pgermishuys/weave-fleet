using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class UserAuthEndpointTests
{
    [Fact]
    public async Task GetUserMe_WhenAuthenticated_ReturnsOk()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<UserMePayload>();
        payload.ShouldNotBeNull();
        payload.UserId.ShouldBe("test-user");
        payload.Email.ShouldBe("test@example.com");
        payload.DisplayName.ShouldBe("Test User");
    }

    [Fact]
    public async Task GetUserMe_WhenUnauthenticated_ReturnsUnauthorized()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/api/user/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateConfig_WithoutCsrfToken_IsRejected()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.PutAsync(
            "/api/config",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateConfig_WithCsrfToken_Succeeds()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var meResponse = await client.GetAsync("/api/user/me");
        meResponse.EnsureSuccessStatusCode();

        var csrfToken = meResponse.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? ExtractCookieValue(setCookies, ".WeaveFleet.CSRF")
            : null;

        csrfToken.ShouldNotBeNull();

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/config")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CSRF-Token", csrfToken);

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Logout_WithoutCsrfToken_IsRejected()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.PostAsync("/auth/logout", new StringContent(string.Empty));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_WithExternalReturnUrl_FallsBackToRoot()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/auth/login?returnUrl=//evil.example/path");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location.OriginalString.ShouldBe("/");
    }

    private static string? ExtractCookieValue(IEnumerable<string> setCookies, string cookieName)
    {
        foreach (var header in setCookies)
        {
            if (!header.StartsWith(cookieName + "=", StringComparison.Ordinal))
                continue;

            var endIndex = header.IndexOf(';');
            return endIndex >= 0
                ? header.Substring(cookieName.Length + 1, endIndex - cookieName.Length - 1)
                : header.Substring(cookieName.Length + 1);
        }

        return null;
    }

    private sealed record UserMePayload(string UserId, string? Email, string? DisplayName, bool OnboardingCompleted, string CreatedAt);
}
