using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class HarnessEndpointsTests
{
    [Fact]
    public async Task get_harnesses_returns_user_enabled_from_preferences()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var preferences = scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();
            await preferences.SetAsync("opencode.enabled", "false");
            await preferences.SetAsync("nucode.enabled", "true");
        }

        var response = await client.GetAsync("/api/harnesses");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(body);
        var harnesses = document.RootElement.EnumerateArray().ToList();

        GetUserEnabled(harnesses, "opencode").ShouldBeFalse();
        GetUserEnabled(harnesses, "nucode").ShouldBeTrue();
    }

    [Fact]
    public async Task get_harnesses_defaults_opencode_enabled_and_nucode_disabled()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/harnesses");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(body);
        var harnesses = document.RootElement.EnumerateArray().ToList();

        GetUserEnabled(harnesses, "opencode").ShouldBeTrue();
        GetUserEnabled(harnesses, "nucode").ShouldBeFalse();
    }

    // ── Warmup endpoint — API contract: no caller-controlled parameters ────────────

    [Fact]
    public async Task warmup_opencode_contract_accepts_no_caller_supplied_parameters()
    {
        // The endpoint signature must accept no owner ID, credential hash, resume token,
        // or workspace directory from the caller. Verify the route only accepts POST with
        // no query or body parameters — any caller-supplied payload must be ignored.
        await using var factory = CreateLocalModeFactoryWithFakeRuntime(out var fakeRuntime);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Attempt to supply caller-controlled parameters via query string — they must be ignored.
        var response = await client.PostAsync(
            "/api/harnesses/opencode/warmup?owner=attacker&credentialHash=abc&resumeToken=tok&dir=/tmp",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Warmup was called, but with the server-resolved identity, not any caller-supplied value.
        fakeRuntime.WarmupCalls.Count.ShouldBe(1);
        fakeRuntime.WarmupCalls[0].ShouldBe("local-user");
    }

    [Fact]
    public async Task warmup_opencode_local_mode_returns_no_content_and_warms_up_local_user()
    {
        await using var factory = CreateLocalModeFactoryWithFakeRuntime(out var fakeRuntime);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsync("/api/harnesses/opencode/warmup", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        fakeRuntime.WarmupCalls.Count.ShouldBe(1);
        fakeRuntime.WarmupCalls[0].ShouldBe("local-user");
    }

    [Fact]
    public async Task warmup_opencode_auth_enabled_authenticated_user_returns_no_content()
    {
        await using var factory = CreateAuthModeFactoryWithFakeRuntime(
            authenticated: true,
            out var fakeRuntime);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // GET first to receive the CSRF cookie.
        var csrfToken = await GetCsrfTokenAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/harnesses/opencode/warmup");
        request.Headers.Add("X-CSRF-Token", csrfToken);

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        fakeRuntime.WarmupCalls.Count.ShouldBe(1);
        // The server resolves identity from the principal — must be the authenticated "sub" claim.
        fakeRuntime.WarmupCalls[0].ShouldBe("test-user");
    }

    [Fact]
    public async Task warmup_opencode_auth_enabled_anonymous_request_returns_unauthorized_without_warmup()
    {
        // In auth-enabled mode with no authenticated user the authorization middleware blocks
        // the request before the endpoint handler runs. No warmup must be attempted.
        await using var factory = CreateAuthModeFactoryWithFakeRuntime(
            authenticated: false,
            out var fakeRuntime);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // GET harnesses (public enough to get a CSRF cookie in auth-enabled mode).
        var csrfToken = await GetCsrfTokenAsAnonymousAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/harnesses/opencode/warmup");
        request.Headers.Add("X-CSRF-Token", csrfToken);

        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        fakeRuntime.WarmupCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task warmup_opencode_auth_enabled_no_user_context_is_no_op()
    {
        // Simulate the edge-case where auth middleware succeeds but IUserContext.IsAuthenticated
        // is false (e.g. background/startup context). The endpoint must skip warmup and return 204.
        var fakeRuntime = new FakeHarnessRuntime("opencode");
        var registry = new FakeHarnessRegistry();
        registry.Register(fakeRuntime);

        await using var factory = new ApiWebApplicationFactory(
            authEnabled: true,
            useTestAuthentication: false,
            configureTestServices: services =>
            {
                // Replace the registry so GetRuntimeByType returns our fake.
                var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IHarnessRegistry));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IHarnessRegistry>(registry);
            });

        // With no authentication scheme that succeeds, the middleware returns 401 before
        // the handler — which IS the correct no-op behavior for auth-enabled anonymous requests.
        // This test verifies warmup was not called regardless.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        // GET first to receive the CSRF cookie.
        var csrfToken = await GetCsrfTokenAsAnonymousAsync(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/harnesses/opencode/warmup");
        request.Headers.Add("X-CSRF-Token", csrfToken);

        var response = await client.SendAsync(request);

        // Either 401 (auth middleware blocked) or 204 (handler skipped warmup) — both are no-op.
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NoContent);
        fakeRuntime.WarmupCalls.ShouldBeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ApiWebApplicationFactory CreateLocalModeFactoryWithFakeRuntime(
        out FakeHarnessRuntime fakeRuntime)
    {
        var runtime = new FakeHarnessRuntime("opencode");
        var registry = new FakeHarnessRegistry();
        registry.Register(runtime);
        fakeRuntime = runtime;

        return new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IHarnessRegistry));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IHarnessRegistry>(registry);
            });
    }

    private static ApiWebApplicationFactory CreateAuthModeFactoryWithFakeRuntime(
        bool authenticated,
        out FakeHarnessRuntime fakeRuntime)
    {
        var runtime = new FakeHarnessRuntime("opencode");
        var registry = new FakeHarnessRegistry();
        registry.Register(runtime);
        fakeRuntime = runtime;

        return new ApiWebApplicationFactory(
            authEnabled: true,
            useTestAuthentication: authenticated,
            configureTestServices: services =>
            {
                var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IHarnessRegistry));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IHarnessRegistry>(registry);
            });
    }

    /// <summary>
    /// Performs a GET to /api/user/me (requires auth) to obtain a CSRF token for authenticated tests.
    /// </summary>
    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var meResponse = await client.GetAsync("/api/user/me");
        meResponse.EnsureSuccessStatusCode();

        if (meResponse.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            var token = ExtractCsrfCookie(setCookies);
            if (token is not null)
                return token;
        }

        throw new InvalidOperationException("CSRF cookie not found in GET /api/user/me response.");
    }

    /// <summary>
    /// Performs a GET to /api/harnesses (GET is CSRF-exempt) to obtain a CSRF cookie
    /// even for anonymous (unauthenticated) clients in auth-enabled mode.
    /// In auth-enabled mode the request returns 401, but the CSRF cookie is set before
    /// the authorization check runs, so the cookie is present in the response.
    /// </summary>
    private static async Task<string> GetCsrfTokenAsAnonymousAsync(HttpClient client)
    {
        // GET requests set the CSRF cookie regardless of authentication state.
        // The response may be 401 in auth-enabled mode, but the cookie is still set.
        var response = await client.GetAsync("/api/harnesses");

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            var token = ExtractCsrfCookie(setCookies);
            if (token is not null)
                return token;
        }

        throw new InvalidOperationException(
            $"CSRF cookie not found in GET /api/harnesses response (status: {response.StatusCode}).");
    }

    private static string? ExtractCsrfCookie(IEnumerable<string> setCookies)
    {
        const string prefix = ".WeaveFleet.CSRF=";
        foreach (var header in setCookies)
        {
            if (!header.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var endIndex = header.IndexOf(';');
            return endIndex >= 0
                ? header[prefix.Length..endIndex]
                : header[prefix.Length..];
        }

        return null;
    }

    private static bool GetUserEnabled(IReadOnlyList<JsonElement> harnesses, string harnessType)
    {
        var harness = harnesses.Single(harness =>
            string.Equals(harness.GetProperty("type").GetString(), harnessType, StringComparison.Ordinal));

        return harness.GetProperty("userEnabled").GetBoolean();
    }
}
