using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WeaveFleet.Api.Auth;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Api.Tests.Auth;

/// <summary>
/// An agent process calls Fleet under <c>/agent/{bridge token}</c>. The verified prefix is its credential, so it gets
/// in on a remote-reachable bind or with <c>RequireToken</c>, where loopback auto-auth is off, and nothing else does.
/// </summary>
[Collection(LocalTokenAuthServiceTestsGroup.Name)]
public sealed class AgentPathAuthTests
{
    private const string AccessToken = "1234567890abcdef";
    private const string BridgeToken = "bridge-1";

    [Theory]
    [InlineData("0.0.0.0", false)]
    [InlineData("::", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    public async Task An_agent_lists_sessions_under_its_prefix_whatever_the_bind(string host, bool requireToken)
    {
        await using var factory = CreateFactory(host, requireToken);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/agent/{BridgeToken}/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_agent_post_to_create_a_session_is_not_refused_as_unauthenticated(bool requireToken)
    {
        await using var factory = CreateFactory("0.0.0.0", requireToken);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/agent/{BridgeToken}/api/sessions",
            new { directory = Path.Combine(Path.GetTempPath(), $"fleet-missing-{Guid.NewGuid():N}") });

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_the_prefix_an_untokened_request_is_still_refused_on_a_remote_reachable_bind()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unknown_bridge_token_is_not_found()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/agent/not-a-token/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_valid_bridge_token_from_another_machine_is_not_found()
    {
        // Without the loopback filter the test server's remote address isn't loopback.
        await using var factory = CreateFactory("0.0.0.0", simulateLocalhost: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/agent/{BridgeToken}/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_wrong_bearer_token_on_the_agent_path_does_not_fall_back_to_agent_auth()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        var response = await client.GetAsync($"/agent/{BridgeToken}/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_agent_request_through_a_proxy_is_refused()
    {
        await using var factory = CreateFactory("0.0.0.0");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.7");

        var response = await client.GetAsync($"/agent/{BridgeToken}/api/sessions");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_agent_principal_does_not_count_as_authenticated_with_the_access_token()
    {
        var result = await AuthenticateThroughAgentPrefixAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal!.HasClaim(BearerTokenHandler.MethodClaim, BearerTokenHandler.AgentMethod).ShouldBeTrue();
        BearerTokenHandler.AuthenticatedWithToken(result.Principal).ShouldBeFalse();
    }

    /// <summary>Runs the real middleware, then the handler on the request it let through, on a 0.0.0.0 bind.</summary>
    private static async Task<AuthenticateResult> AuthenticateThroughAgentPrefixAsync()
    {
        var services = new ServiceCollection()
            .AddSingleton<IHarnessBridgeTokens>(new FakeBridgeTokens())
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        AuthenticateResult? result = null;
        app.UseAgentRequests();
        app.Run(async context =>
        {
            var handler = new BearerTokenHandler(
                new StaticOptionsMonitor(),
                NullLoggerFactory.Instance,
                UrlEncoder.Default,
                new StubLocalTokenAuthService(AccessToken),
                new LoopbackAuthPolicy("0.0.0.0"));
            await handler.InitializeAsync(
                new AuthenticationScheme(BearerTokenHandler.SchemeName, null, typeof(BearerTokenHandler)), context);
            result = await handler.AuthenticateAsync();
        });

        var http = new DefaultHttpContext { RequestServices = services };
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Path = $"/agent/{BridgeToken}/api/sessions";
        await app.Build()(http);

        result.ShouldNotBeNull();
        return result;
    }

    private static ApiWebApplicationFactory CreateFactory(string host, bool requireToken = false, bool simulateLocalhost = true)
        => new(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: simulateLocalhost,
            host: host,
            requireToken: requireToken,
            configureTestServices: services =>
            {
                services.AddSingleton<ILocalTokenAuthService>(new StubLocalTokenAuthService(AccessToken));
                services.AddSingleton<IHarnessBridgeTokens>(new FakeBridgeTokens());
            });

    private sealed class FakeBridgeTokens : IHarnessBridgeTokens
    {
        public bool IsKnown(string bridgeToken) => bridgeToken == BridgeToken;
    }

    private sealed class StubLocalTokenAuthService(string token) : ILocalTokenAuthService
    {
        public string Token { get; } = token;

        public bool ValidateToken(string candidate) => string.Equals(candidate, Token, StringComparison.Ordinal);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();

        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
