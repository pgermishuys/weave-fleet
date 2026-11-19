using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// <c>fleet node</c>: the API without the web app, and the token on every request. Set up the way <c>--node</c>
/// sets it (<c>Fleet:ServeUi=false</c>, <c>Fleet:Auth:RequireToken=true</c>), bound to loopback, with every request
/// arriving over loopback: a node never signs anyone in by where they come from.
/// </summary>
public sealed class NodeModeEndpointTests :
    IClassFixture<NodeModeEndpointTests.Server>,
    IClassFixture<NodeModeEndpointTests.WebAppServer>,
    IClassFixture<NodeModeEndpointTests.ElsewhereServer>
{
    private readonly ApiWebApplicationFactory _node;
    private readonly ApiWebApplicationFactory _webApp;
    private readonly ApiWebApplicationFactory _elsewhere;

    public NodeModeEndpointTests(Server node, WebAppServer webApp, ElsewhereServer elsewhere)
    {
        _node = node.Factory;
        _webApp = webApp.Factory;
        _elsewhere = elsewhere.Factory;
    }

    // `_node` gets every request from this computer, like the desktop app's window. `_elsewhere` gets them from
    // another machine: the web app is not for that caller.

    [Fact]
    public async Task A_browser_at_the_root_gets_a_page_saying_what_this_is()
    {
        var factory = _elsewhere;
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var response = await client.GetAsync("/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        var page = await response.Content.ReadAsStringAsync();
        page.ShouldContain("Fleet machine");
        page.ShouldContain("fleet node pair");
        page.ShouldContain("Settings → Machines → Add a machine");
        page.ShouldNotContain("access token");
        page.ShouldNotContain("<script");
        page.ShouldContain("src=\"data:image/png;base64,");
        page.ShouldContain(NodeEndpoints.DocsUrl);
        page.ShouldNotContain("{{");
        page.ShouldNotContain("<div id=\"app\">");
    }

    [Fact]
    public async Task A_pairing_link_opened_in_a_browser_gets_the_same_page()
    {
        using var client = CreateClient(_elsewhere);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        var response = await client.GetAsync("/add-machine");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("paste the link it printed");
    }

    [Fact]
    public async Task Anything_else_at_the_root_gets_a_json_note()
    {
        var factory = _elsewhere;
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        var note = await response.Content.ReadFromJsonAsync<JsonElement>();
        note.GetProperty("kind").GetString().ShouldBe("fleet-node");
        note.GetProperty("message").GetString()!.ShouldContain("fleet node pair");
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
        var factory = _elsewhere;
        using var client = CreateClient(factory, Token(factory));

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_web_app_is_still_served_without_the_switch()
    {
        var factory = _webApp;
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
        var factory = _node;
        using var client = CreateClient(factory);

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_protected_page_outside_the_api_answers_401_rather_than_sending_to_a_sign_in_page()
    {
        var factory = _node;
        using var client = CreateClient(factory);

        var response = await client.PostAsync("/auth/logout", new StringContent(string.Empty));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task The_machine_answers_with_the_token()
    {
        var factory = _node;
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
        var factory = _node;
        using var client = CreateClient(factory);

        var withToken = await client.PostAsync(
            $"/hubs/session-events/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(Token(factory))}",
            new StringContent(string.Empty));
        var without = await client.PostAsync("/hubs/session-events/negotiate?negotiateVersion=1", new StringContent(string.Empty));

        withToken.StatusCode.ShouldBe(HttpStatusCode.OK);
        without.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_node_makes_a_code_a_phone_can_scan()
    {
        var factory = _node;
        using var client = CreateClient(factory, Token(factory));

        var response = await client.PostAsJsonAsync("/api/machine/pairing", new { baseUrl = "https://atlas.tail9c2e.ts.net" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var made = await response.Content.ReadFromJsonAsync<JsonElement>();
        var payload = made.GetProperty("payload");
        payload.GetProperty("url").GetString().ShouldBe("https://atlas.tail9c2e.ts.net");
        payload.GetProperty("urls").EnumerateArray().Select(u => u.GetString()).ShouldBe(["https://atlas.tail9c2e.ts.net"]);
        made.GetProperty("url").GetString().ShouldStartWith("https://atlas.tail9c2e.ts.net/pair#p=");
    }

    [Fact]
    public async Task A_node_that_only_listens_on_loopback_has_no_address_to_put_in_a_code()
    {
        var factory = _node;
        using var client = CreateClient(factory, Token(factory));

        var response = await client.PostAsJsonAsync("/api/machine/pairing", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString()!.ShouldContain("only listens on this computer");
    }

    // ── The web app, for this computer's own desktop app ──────────────────────

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/sessions/9b2d")]
    [InlineData("/settings")]
    public async Task This_computer_gets_the_web_app(string path)
    {
        using var client = CreateClient(_node);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");

        var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("<div id=\"app\">");
    }

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("Tailscale-User-Login", "alex@example.com")]
    public async Task A_request_that_came_through_a_proxy_is_not_from_this_computer(string header, string value)
    {
        using var client = CreateClient(_node);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        client.DefaultRequestHeaders.Add(header, value);

        var root = await client.GetAsync("/");
        var deepLink = await client.GetAsync("/sessions/9b2d");

        (await root.Content.ReadAsStringAsync()).ShouldContain("Fleet machine");
        deepLink.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_web_app_on_a_node_still_needs_the_key_for_everything_it_asks()
    {
        using var client = CreateClient(_node);

        (await client.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/auth/token-login", new { token = "not-the-key-not-the-key-not-the-key" })).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_key_in_the_machine_file_signs_the_app_in()
    {
        var identity = _node.Services.GetRequiredService<MachineIdentityStore>();
        var key = JsonDocument.Parse(await File.ReadAllTextAsync(identity.FilePath)).RootElement.GetProperty("accessToken").GetString()!;
        key.ShouldBe(Token(_node));

        using var client = _node.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var login = await client.PostAsJsonAsync("/auth/token-login", new { token = key });

        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/api/sessions")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── What it prints at startup ─────────────────────────────────────────────

    [Fact]
    public void A_node_on_the_network_prints_its_addresses_and_token()
    {
        var options = new FleetOptions { Host = "0.0.0.0", Port = 5512 };

        var lines = NodeEndpoints.StartupLines(options, new LoopbackAuthPolicy(options.Host, requireToken: true), new StubTokens("atlas-token-0123456789", LocalTokenSource.Saved), "/data/fleet.machine.json");

        lines.ShouldContain("    fleet node pair");
        lines.ShouldContain(line => line.Contains("paste the link it prints into Settings → Machines → Add a machine", StringComparison.Ordinal));
        lines.ShouldContain("  Scripts can use the master token in /data/fleet.machine.json.");
        lines.ShouldNotContain(line => line.Contains("atlas-token-0123456789", StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.Contains("can't reach it", StringComparison.Ordinal));
    }

    [Fact]
    public void A_node_on_loopback_says_other_machines_cannot_reach_it_yet()
    {
        var options = new FleetOptions { Host = "127.0.0.1", Port = 5512 };

        var lines = NodeEndpoints.StartupLines(options, new LoopbackAuthPolicy(options.Host, requireToken: true), new StubTokens("atlas-token-0123456789", LocalTokenSource.Environment), "/data/fleet.machine.json");

        lines.ShouldContain(line => line.Contains("other machines can't reach it directly yet", StringComparison.Ordinal));
        lines.ShouldNotContain("    fleet node pair");
        lines.ShouldContain("  Scripts can use the master token in WEAVE_FLEET_AUTH_TOKEN.");
    }

    private static ApiWebApplicationFactory CreateNode()
        => new(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            host: "127.0.0.1",
            requireToken: true,
            serveUi: false);

    public sealed class Server : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = CreateNode();

        public void Dispose() => Factory.Dispose();
    }

    public sealed class ElsewhereServer : IDisposable
    {
        // Requests carry no loopback address, like one from another machine.
        public ApiWebApplicationFactory Factory { get; } = new(
            authEnabled: false,
            tokenAuthEnabled: true,
            host: "127.0.0.1",
            requireToken: true,
            serveUi: false);

        public void Dispose() => Factory.Dispose();
    }

    public sealed class WebAppServer : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = new(authEnabled: false, tokenAuthEnabled: true, simulateLocalhostRequest: true, host: "127.0.0.1");

        public void Dispose() => Factory.Dispose();
    }

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
