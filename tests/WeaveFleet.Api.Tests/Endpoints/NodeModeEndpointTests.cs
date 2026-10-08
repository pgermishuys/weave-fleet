using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Tests.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// <c>fleet node</c>: the API without the web app, and the token on every request. Set up the way <c>--node</c>
/// sets it (<c>Fleet:ServeUi=false</c>, <c>Fleet:Auth:RequireToken=true</c>), bound to loopback, with every request
/// arriving over loopback: a node never signs anyone in by where they come from.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class NodeModeEndpointTests
{
    [Fact]
    public async Task A_browser_at_the_root_gets_a_page_saying_what_this_is()
    {
        await using var factory = CreateNode();
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var response = await client.GetAsync("/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        var page = await response.Content.ReadAsStringAsync();
        page.ShouldContain("Fleet node");
        page.ShouldContain("Settings → Machines → Add a machine");
        page.ShouldContain("src=\"data:image/png;base64,");
        page.ShouldContain(NodeEndpoints.DocsUrl);
        page.ShouldNotContain("{{");
        page.ShouldNotContain("<div id=\"app\">");
    }

    [Fact]
    public async Task Anything_else_at_the_root_gets_a_json_note()
    {
        await using var factory = CreateNode();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        var note = await response.Content.ReadFromJsonAsync<JsonElement>();
        note.GetProperty("kind").GetString().ShouldBe("fleet-node");
        note.GetProperty("message").GetString()!.ShouldContain("Settings → Machines");
        note.GetProperty("docs").GetString().ShouldBe(NodeEndpoints.DocsUrl);
    }

    [Theory]
    [InlineData("/index.html")]
    [InlineData("/app.js")]
    [InlineData("/login")]
    [InlineData("/login?token=abc")]
    [InlineData("/sessions/9b2d")]
    [InlineData("/pair")]
    public async Task Web_app_paths_are_not_found(string path)
    {
        await using var factory = CreateNode();
        using var client = CreateClient(factory, Token(factory));

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_web_app_is_still_served_without_the_switch()
    {
        await using var factory = new ApiWebApplicationFactory(authEnabled: false, tokenAuthEnabled: true, simulateLocalhostRequest: true, host: "127.0.0.1");
        using var client = CreateClient(factory);

        var root = await client.GetStringAsync("/");
        var deepLink = await client.GetStringAsync("/sessions/9b2d");

        root.ShouldContain("<div id=\"app\">");
        deepLink.ShouldContain("<div id=\"app\">");
    }

    [Theory]
    [InlineData("/api/machine")]
    [InlineData("/api/sessions")]
    [InlineData("/api/user/me")]
    public async Task Api_requests_without_the_token_are_refused_even_over_loopback(string path)
    {
        await using var factory = CreateNode();
        using var client = CreateClient(factory);

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_protected_page_outside_the_api_answers_401_rather_than_sending_to_a_sign_in_page()
    {
        await using var factory = CreateNode();
        using var client = CreateClient(factory);

        var response = await client.PostAsync("/auth/logout", new StringContent(string.Empty));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task The_machine_answers_with_the_token()
    {
        await using var factory = CreateNode();
        using var client = CreateClient(factory, Token(factory));

        var machine = await client.GetFromJsonAsync<JsonElement>("/api/machine");

        machine.GetProperty("apiVersion").GetInt32().ShouldBe(MachineEndpoints.ApiVersion);
        machine.GetProperty("authMode").GetString().ShouldBe("token");
        machine.GetProperty("requiresToken").GetBoolean().ShouldBeTrue();
        machine.GetProperty("webApp").GetBoolean().ShouldBeFalse();
        (await client.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_hub_takes_the_token_as_access_token()
    {
        await using var factory = CreateNode();
        using var client = CreateClient(factory);

        var withToken = await client.PostAsync(
            $"/hubs/session-events/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(Token(factory))}",
            new StringContent(string.Empty));
        var without = await client.PostAsync("/hubs/session-events/negotiate?negotiateVersion=1", new StringContent(string.Empty));

        withToken.StatusCode.ShouldBe(HttpStatusCode.OK);
        without.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pairing_a_phone_points_at_a_fleet_with_the_web_app()
    {
        await using var factory = CreateNode();
        using var client = CreateClient(factory, Token(factory));

        var response = await client.PostAsJsonAsync("/api/machine/pairing", new { baseUrl = "https://atlas.tail9c2e.ts.net" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString()!.ShouldContain("needs a Fleet with the web app");
    }

    // ── What it prints at startup ─────────────────────────────────────────────

    [Fact]
    public void A_node_on_the_network_prints_its_addresses_and_token()
    {
        var options = new FleetOptions { Host = "0.0.0.0", Port = 5512 };

        var lines = NodeEndpoints.StartupLines(options, new LoopbackAuthPolicy(options.Host, requireToken: true), new StubTokens("atlas-token-0123456789", LocalTokenSource.Saved), "/data/fleet.machine.json");

        lines.ShouldContain(line => line.Contains("Settings → Machines → Add a machine", StringComparison.Ordinal));
        lines.ShouldContain(line => line.StartsWith("    URL:  ", StringComparison.Ordinal) && line.EndsWith(":5512", StringComparison.Ordinal));
        lines.ShouldContain("    Token:  atlas-token-0123456789");
        lines.ShouldContain("  Fleet keeps the token in /data/fleet.machine.json, so it stays the same after a restart.");
        lines.ShouldNotContain(line => line.Contains("can't reach it", StringComparison.Ordinal));
    }

    [Fact]
    public void A_node_on_loopback_says_other_machines_cannot_reach_it_yet()
    {
        var options = new FleetOptions { Host = "127.0.0.1", Port = 5512 };

        var lines = NodeEndpoints.StartupLines(options, new LoopbackAuthPolicy(options.Host, requireToken: true), new StubTokens("atlas-token-0123456789", LocalTokenSource.Environment), "/data/fleet.machine.json");

        lines.ShouldContain("    URL:    http://localhost:5512");
        lines.ShouldContain(line => line.Contains("other machines can't reach it yet", StringComparison.Ordinal));
        lines.ShouldContain("  WEAVE_FLEET_AUTH_TOKEN sets the token.");
    }

    private static ApiWebApplicationFactory CreateNode()
        => new(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            host: "127.0.0.1",
            requireToken: true,
            serveUi: false);

    private static string Token(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory, string? token = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed class StubTokens(string token, LocalTokenSource source) : ILocalTokenAuthService
    {
        public string Token { get; } = token;

        public LocalTokenSource Source { get; } = source;

        public bool ValidateToken(string candidate) => string.Equals(candidate, Token, StringComparison.Ordinal);
    }
}
