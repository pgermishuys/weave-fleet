using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Usage;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class UsageEndpointsTests(UsageEndpointsTests.Server server) : IClassFixture<UsageEndpointsTests.Server>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("fleet-usage-api-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public async Task It_is_always_enabled_and_a_provider_without_consent_is_not_read()
    {
        using var client = server.Factory.CreateClient();

        var text = await client.GetStringAsync("/api/usage/limits?watch=true");
        using var body = JsonDocument.Parse(text);

        body.RootElement.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        var codex = body.RootElement.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("provider").GetString() == "codex");
        codex.GetProperty("state").GetString().ShouldBe("not_allowed");
        codex.GetProperty("accountKey").GetString().ShouldBe(WeaveFleet.Application.Usage.AccountKey.For("codex", "acct-77"));
        text.ShouldNotContain("invented-secret");
        text.ShouldNotContain("acct-77");
    }

    [Fact]
    public async Task A_provider_with_no_account_id_has_a_null_key_that_is_still_sent()
    {
        using var client = server.Factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/usage/limits");

        body.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("provider").GetString() == "claude")
            .GetProperty("accountKey").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task On_it_lists_the_sign_in_it_found_and_never_the_token()
    {
        using var client = server.Factory.CreateClient();

        // The first read happens by itself; this token expired long ago, so it answers signed out without any request.
        var text = await client.GetStringAsync("/api/usage/limits");
        using var body = JsonDocument.Parse(text);

        body.RootElement.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        var claude = body.RootElement.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("provider").GetString() == "claude");
        claude.GetProperty("name").GetString().ShouldBe("Claude");
        claude.GetProperty("state").GetString().ShouldBe("signed_out");
        claude.GetProperty("source").GetString().ShouldBe("Claude Code");
        text.ShouldNotContain("invented-secret");
    }

    // Its own server: the poll needs a sign-in without an expiry, and a canned answer from the provider.
    [Fact]
    public async Task A_polled_window_carries_its_length()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        await File.WriteAllTextAsync(Path.Combine(_home, ".claude", ".credentials.json"), """{"claudeAiOauth":{"accessToken":"invented-secret"}}""");
        await using var factory = new ApiWebApplicationFactory(authEnabled: false, configureTestServices: services =>
        {
            services.RemoveAll<UsageSources>();
            services.AddSingleton(new UsageSources(_home, _ => null));
            services.AddHttpClient(UsageSources.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new CannedHandler("""{"five_hour":{"utilization":40,"resets_at":"2030-01-01T00:00:00Z"}}"""));
        });
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/usage/limits?watch=true");

        var window = body.GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("provider").GetString() == "claude").GetProperty("windows")[0];
        window.GetProperty("window").GetString().ShouldBe("five_hour");
        window.GetProperty("windowSeconds").GetInt32().ShouldBe(18000);
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task Allow_and_revoke_answer_204_and_an_unknown_provider_404()
    {
        var factory = server.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        (await client.PostAsync("/api/usage/limits/codex/allow", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var scope = factory.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync("UsageAllow.codex")).ShouldBe("true");

        (await client.DeleteAsync("/api/usage/limits/codex/allow")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var scope = factory.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync("UsageAllow.codex")).ShouldBe("false");

        (await client.PostAsync("/api/usage/limits/nope/allow", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The readers look in a temp home, never the real one.
    public sealed class Server : IDisposable
    {
        private readonly string _home = Directory.CreateTempSubdirectory("fleet-usage-api-server-").FullName;

        public ApiWebApplicationFactory Factory { get; }

        public Server()
        {
            Directory.CreateDirectory(Path.Combine(_home, ".local", "share", "opencode"));
            File.WriteAllText(Path.Combine(_home, ".local", "share", "opencode", "auth.json"),
                """{"openai":{"type":"oauth","access":"invented-secret","accountId":"acct-77"}}""");
            Directory.CreateDirectory(Path.Combine(_home, ".claude"));
            File.WriteAllText(Path.Combine(_home, ".claude", ".credentials.json"), """{"claudeAiOauth":{"accessToken":"invented-secret","expiresAt":1000}}""");
            Factory = new ApiWebApplicationFactory(authEnabled: false, configureTestServices: services =>
            {
                services.RemoveAll<UsageSources>();
                services.AddSingleton(new UsageSources(_home, _ => null));
            });
        }

        public void Dispose()
        {
            Factory.Dispose();
            Directory.Delete(_home, recursive: true);
        }
    }
}
