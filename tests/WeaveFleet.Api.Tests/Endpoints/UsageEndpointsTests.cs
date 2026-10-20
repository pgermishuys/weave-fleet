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

public sealed class UsageEndpointsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("fleet-usage-api-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    // The readers look in a temp home, never the real one.
    private ApiWebApplicationFactory Factory() => new(authEnabled: false, configureTestServices: services =>
    {
        services.RemoveAll<UsageSources>();
        services.AddSingleton(new UsageSources(_home, _ => null));
    });

    [Fact]
    public async Task Off_by_default_it_answers_enabled_false_and_reads_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        await File.WriteAllTextAsync(Path.Combine(_home, ".claude", ".credentials.json"), """{"claudeAiOauth":{"accessToken":"invented"}}""");
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/usage/limits?watch=true");

        body.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        body.GetProperty("providers").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task On_it_lists_the_sign_in_it_found_and_never_the_token()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        await File.WriteAllTextAsync(Path.Combine(_home, ".claude", ".credentials.json"), """{"claudeAiOauth":{"accessToken":"invented-secret","expiresAt":1000}}""");
        await using var factory = Factory();
        using var client = factory.CreateClient();
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync("UsageLimits", "true");

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
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync("UsageLimits", "true");

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
        await using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        (await client.PostAsync("/api/usage/limits/codex/allow", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var scope = factory.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync("UsageAllow.codex")).ShouldBe("true");

        (await client.DeleteAsync("/api/usage/limits/codex/allow")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await using (var scope = factory.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().GetAsync("UsageAllow.codex")).ShouldBe("false");

        (await client.PostAsync("/api/usage/limits/nope/allow", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
