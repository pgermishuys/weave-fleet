using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Auth;

/// <summary>
/// A Fleet page on one machine calling the Fleet on another: the identity it reads, the token it presents, the
/// CORS answer the browser needs, and the socket forms of the token. Every request here arrives over loopback,
/// the way <c>tailscale serve</c> delivers it, so none of these pass by ambient trust.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class MachineAccessEndpointTests
{
    private const string ForeignOrigin = "http://falcon.tail9c2e.ts.net:2113";

    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Machine_identity_is_stable_and_names_its_contract()
    {
        await using var factory = CreateFactory("127.0.0.1");
        using var client = CreateClient(factory);

        var first = await client.GetFromJsonAsync<JsonElement>("/api/machine");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/machine");

        first.GetProperty("id").GetString().ShouldNotBeNullOrEmpty();
        first.GetProperty("id").GetString().ShouldBe(second.GetProperty("id").GetString());
        first.GetProperty("apiVersion").GetInt32().ShouldBe(MachineEndpoints.ApiVersion);
        first.GetProperty("authMode").GetString().ShouldBe("token");
        first.GetProperty("name").GetString().ShouldBe(Environment.MachineName);
        first.GetProperty("remoteReachable").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_machine_can_be_renamed_and_an_empty_name_goes_back_to_the_host_name()
    {
        await using var factory = CreateFactory("127.0.0.1");
        using var client = CreateClient(factory);

        var renamed = await client.PutAsJsonAsync("/api/machine", new { name = "  hangar  " });
        (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString().ShouldBe("hangar");

        var reset = await client.PutAsJsonAsync("/api/machine", new { name = "" });
        (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString().ShouldBe(Environment.MachineName);
    }

    [Fact]
    public async Task Machine_identity_needs_the_token_on_a_reachable_bind()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);

        (await client.GetAsync("/api/machine")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(factory));
        (await client.GetAsync("/api/machine")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── The token ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Access_reports_the_saved_token()
    {
        await using var factory = CreateFactory("127.0.0.1");
        using var client = CreateClient(factory);

        var access = await client.GetFromJsonAsync<JsonElement>("/api/machine/access");

        access.GetProperty("token").GetString().ShouldBe(Token(factory));
        access.GetProperty("tokenSource").GetString().ShouldBe("saved");
        access.GetProperty("addresses").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Access_lists_addresses_when_other_devices_can_connect()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory, Token(factory));

        var access = await client.GetFromJsonAsync<JsonElement>("/api/machine/access");

        access.GetProperty("remoteReachable").GetBoolean().ShouldBeTrue();
        access.GetProperty("addresses").EnumerateArray()
            .ShouldContain(address => address.GetProperty("kind").GetString() == "hostname");
    }

    [Fact]
    public async Task Replacing_the_token_locks_out_the_old_one()
    {
        await using var factory = CreateFactory("0.0.0.0");
        var old = Token(factory);
        using var client = CreateClient(factory, old);

        var replaced = await client.PostAsync("/api/machine/access/token", content: null);
        replaced.StatusCode.ShouldBe(HttpStatusCode.OK);
        var next = (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        next.ShouldNotBe(old);
        (await client.GetAsync("/api/machine")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var withNew = CreateClient(factory, next);
        (await withNew.GetAsync("/api/machine")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ── CORS for a page on another machine ────────────────────────────────────

    [Fact]
    public async Task A_preflight_for_a_tokened_request_is_answered_for_any_origin()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);

        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/sessions");
        request.Headers.Add("Origin", ForeignOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ForeignOrigin);
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    [Fact]
    public async Task A_tokened_request_from_another_origin_can_be_read()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory, Token(factory));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/machine");
        request.Headers.Add("Origin", ForeignOrigin);
        var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().ShouldBe(ForeignOrigin);
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    [Fact]
    public async Task An_untokened_request_from_another_origin_gets_no_cors_answer_even_when_loopback_lets_it_in()
    {
        // The desktop app's shape: loopback auto-auth signs this request in. Without CORS headers the browser
        // still won't hand the answer to a page on another origin.
        await using var factory = CreateFactory("127.0.0.1");
        using var client = CreateClient(factory);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/machine/access");
        request.Headers.Add("Origin", "http://evil.example");
        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    // ── The token on sockets ──────────────────────────────────────────────────

    [Fact]
    public async Task The_hub_accepts_the_token_as_access_token()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);

        var accepted = await client.PostAsync(
            $"/hubs/session-events/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(Token(factory))}",
            new StringContent(string.Empty));
        var refused = await client.PostAsync(
            "/hubs/session-events/negotiate?negotiateVersion=1&access_token=wrong-token-0123456789",
            new StringContent(string.Empty));

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ordinary_api_requests_ignore_a_token_in_the_query()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = CreateClient(factory);

        var response = await client.GetAsync($"/api/machine?access_token={Uri.EscapeDataString(Token(factory))}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_query_token_never_falls_back_to_loopback()
    {
        await using var factory = CreateFactory("127.0.0.1");
        using var client = CreateClient(factory);

        var response = await client.PostAsync(
            "/hubs/session-events/negotiate?negotiateVersion=1&access_token=wrong-token-0123456789",
            new StringContent(string.Empty));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("websocket", true)]
    [InlineData(null, false)]
    public void A_socket_upgrade_on_an_api_path_carries_its_token_in_the_query(string? upgrade, bool expected)
    {
        // Terminal sockets live under /api. Authentication runs before UseWebSockets, so the upgrade has to be
        // recognised from its headers.
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/sessions/s1/terminals/t1/socket";
        context.Request.QueryString = new QueryString("?access_token=some-token-0123456789");
        if (upgrade is not null)
        {
            context.Request.Headers.Connection = "Upgrade";
            context.Request.Headers.Upgrade = upgrade;
        }

        BearerTokenHandler.PresentsToken(context.Request).ShouldBe(expected);
    }

    [Fact]
    public void A_terminal_opened_with_the_token_may_come_from_another_origin()
    {
        var options = new FleetOptions();
        var withToken = TerminalRequest(ForeignOrigin, BearerTokenHandler.TokenMethod);
        var byLoopback = TerminalRequest(ForeignOrigin, BearerTokenHandler.LoopbackMethod);

        TerminalSocket.IsOriginAllowed(withToken, options, development: false).ShouldBeTrue();
        TerminalSocket.IsOriginAllowed(byLoopback, options, development: false).ShouldBeFalse();
    }

    // ── Behind a proxy ────────────────────────────────────────────────────────

    [Fact]
    public async Task Require_token_turns_off_loopback_auto_auth()
    {
        await using var factory = CreateFactory("127.0.0.1", requireToken: true);
        using var client = CreateClient(factory);

        (await client.GetAsync("/api/user/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(factory));
        (await client.GetAsync("/api/user/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "100.101.102.103")]
    [InlineData("Forwarded", "for=100.101.102.103")]
    [InlineData("Tailscale-User-Login", "someone@example.com")]
    public async Task A_loopback_request_that_came_through_a_proxy_needs_the_token(string header, string value)
    {
        await using var factory = CreateFactory("127.0.0.1");
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add(header, value);

        (await client.GetAsync("/api/user/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static DefaultHttpContext TerminalRequest(string origin, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1:2113");
        context.Request.Headers.Origin = origin;
        context.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(BearerTokenHandler.MethodClaim, method)], BearerTokenHandler.SchemeName));
        return context;
    }

    private static string Token(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

    private static ApiWebApplicationFactory CreateFactory(string host, bool requireToken = false)
        => new(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            host: host,
            requireToken: requireToken);

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
}
