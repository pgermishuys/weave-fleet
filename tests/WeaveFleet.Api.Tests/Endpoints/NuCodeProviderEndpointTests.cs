using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class NuCodeProviderEndpointTests
{
    private static HttpClient CreateClient(ApiWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    // ── GET /api/nucode/providers ─────────────────────────────────────────────

    [Fact]
    public async Task list_providers_returns_ok_with_all_built_in_providers()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/nucode/providers");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var providers = doc.RootElement.EnumerateArray().ToList();

        providers.Count.ShouldBeGreaterThanOrEqualTo(4);
        providers.ShouldContain(p => p.GetProperty("id").GetString() == "anthropic");
        providers.ShouldContain(p => p.GetProperty("id").GetString() == "openai");
        providers.ShouldContain(p => p.GetProperty("id").GetString() == "copilot");
    }

    [Fact]
    public async Task list_providers_returns_is_connected_false_when_no_credentials_stored()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/nucode/providers");
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        var anthropic = doc.RootElement.EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("id").GetString() == "anthropic");

        anthropic.ValueKind.ShouldNotBe(JsonValueKind.Undefined);
        anthropic.GetProperty("isConnected").GetBoolean().ShouldBeFalse();
    }

    // ── GET /api/nucode/providers/{id} ────────────────────────────────────────

    [Fact]
    public async Task get_provider_returns_ok_for_known_provider()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/nucode/providers/anthropic");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("id").GetString().ShouldBe("anthropic");
        doc.RootElement.GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task get_provider_returns_404_for_unknown_provider()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/nucode/providers/does-not-exist");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── PUT /api/nucode/providers/{id}/credentials ────────────────────────────

    [Fact]
    public async Task store_credentials_returns_204_for_known_provider()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var csrfToken = await GetCsrfTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/nucode/providers/anthropic/credentials");
        request.Headers.Add("X-CSRF-Token", csrfToken);
        request.Content = JsonContent.Create(new { fields = new Dictionary<string, string> { ["apiKey"] = "sk-ant-test" } });

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task store_credentials_returns_404_for_unknown_provider()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var csrfToken = await GetCsrfTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/nucode/providers/does-not-exist/credentials");
        request.Headers.Add("X-CSRF-Token", csrfToken);
        request.Content = JsonContent.Create(new { fields = new Dictionary<string, string> { ["apiKey"] = "sk-test" } });

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task store_credentials_then_provider_shows_as_connected()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var csrfToken = await GetCsrfTokenAsync(client);

        // Store credentials
        using var storeRequest = new HttpRequestMessage(HttpMethod.Put, "/api/nucode/providers/anthropic/credentials");
        storeRequest.Headers.Add("X-CSRF-Token", csrfToken);
        storeRequest.Content = JsonContent.Create(new { fields = new Dictionary<string, string> { ["apiKey"] = "sk-ant-test" } });
        await client.SendAsync(storeRequest);

        // Check provider detail
        var detailResponse = await client.GetAsync("/api/nucode/providers/anthropic");
        var body = await detailResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        doc.RootElement.GetProperty("isConnected").GetBoolean().ShouldBeTrue();
    }

    // ── DELETE /api/nucode/providers/{id}/credentials ─────────────────────────

    [Fact]
    public async Task disconnect_provider_returns_204()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var csrfToken = await GetCsrfTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/nucode/providers/anthropic/credentials");
        request.Headers.Add("X-CSRF-Token", csrfToken);

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task disconnect_provider_removes_credentials()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var csrfToken = await GetCsrfTokenAsync(client);

        // Store credentials
        using var storeRequest = new HttpRequestMessage(HttpMethod.Put, "/api/nucode/providers/anthropic/credentials");
        storeRequest.Headers.Add("X-CSRF-Token", csrfToken);
        storeRequest.Content = JsonContent.Create(new { fields = new Dictionary<string, string> { ["apiKey"] = "sk-ant-test" } });
        await client.SendAsync(storeRequest);

        // Disconnect
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/api/nucode/providers/anthropic/credentials");
        deleteRequest.Headers.Add("X-CSRF-Token", csrfToken);
        await client.SendAsync(deleteRequest);

        // Verify disconnected
        var detailResponse = await client.GetAsync("/api/nucode/providers/anthropic");
        var body = await detailResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        doc.RootElement.GetProperty("isConnected").GetBoolean().ShouldBeFalse();
    }

    // ── PUT /api/nucode/providers/{id}/config ─────────────────────────────────

    [Fact]
    public async Task configure_provider_returns_204_for_known_provider()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var csrfToken = await GetCsrfTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/nucode/providers/openai/config");
        request.Headers.Add("X-CSRF-Token", csrfToken);
        request.Content = JsonContent.Create(new { options = new Dictionary<string, string> { ["baseUrl"] = "http://localhost:11434/v1" } });

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task configure_provider_returns_404_for_unknown_provider()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        using var client = CreateClient(factory);

        var csrfToken = await GetCsrfTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/nucode/providers/does-not-exist/config");
        request.Headers.Add("X-CSRF-Token", csrfToken);
        request.Content = JsonContent.Create(new { options = new Dictionary<string, string> { ["baseUrl"] = "http://localhost:11434/v1" } });

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var meResponse = await client.GetAsync("/api/user/me");
        meResponse.EnsureSuccessStatusCode();

        var csrfToken = meResponse.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? ExtractCookieValue(setCookies, ".WeaveFleet.CSRF")
            : null;

        csrfToken.ShouldNotBeNull();
        return csrfToken;
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
}
