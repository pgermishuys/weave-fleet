using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class ConfigEndpointTests
{
    [Fact]
    public async Task get_config_returns_pooled_opencode_harness_disabled_by_default()
    {
        await using var factory = CreateFactoryWithConfigDirectory(CreateTempConfigDirectory());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/api/config");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        payload.ShouldNotBeNull();
        payload["pooledOpenCodeHarness"].ShouldNotBeNull();
        payload["pooledOpenCodeHarness"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task put_config_toggles_pooled_opencode_harness_at_runtime()
    {
        var configDirectory = CreateTempConfigDirectory();
        await using var factory = CreateFactoryWithConfigDirectory(configDirectory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var enableResponse = await client.PutAsJsonAsync("/api/config", new JsonObject
        {
            ["pooledOpenCodeHarness"] = true,
        });

        enableResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var options = factory.Services.GetRequiredService<FleetOptions>();
        options.Harness.PooledOpenCodeHarness.ShouldBeTrue();

        var getEnabledResponse = await client.GetAsync("/api/config");
        getEnabledResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var enabledPayload = await getEnabledResponse.Content.ReadFromJsonAsync<JsonObject>();
        enabledPayload.ShouldNotBeNull();
        enabledPayload["pooledOpenCodeHarness"]!.GetValue<bool>().ShouldBeTrue();

        var disableResponse = await client.PutAsJsonAsync("/api/config", new JsonObject
        {
            ["pooledOpenCodeHarness"] = false,
        });

        disableResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        options.Harness.PooledOpenCodeHarness.ShouldBeFalse();
    }

    [Fact]
    public async Task put_config_rejects_non_boolean_pooled_opencode_harness_value()
    {
        await using var factory = CreateFactoryWithConfigDirectory(CreateTempConfigDirectory());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.PutAsJsonAsync("/api/config", new JsonObject
        {
            ["pooledOpenCodeHarness"] = "true",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var options = factory.Services.GetRequiredService<FleetOptions>();
        options.Harness.PooledOpenCodeHarness.ShouldBeFalse();
    }

    private static ApiWebApplicationFactory CreateFactoryWithConfigDirectory(string configDirectory)
    {
        return new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services =>
            {
                services.RemoveAll<ConfigService>();
                services.AddSingleton(sp => new ConfigService(
                    sp.GetRequiredService<ILogger<ConfigService>>(),
                    new ConfigPaths(configDirectory, Path.Combine(configDirectory, "weave-opencode.jsonc"))));
            });
    }

    private static string CreateTempConfigDirectory()
    {
        var configDirectory = Path.Combine(Path.GetTempPath(), $"fleet-config-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configDirectory);
        return configDirectory;
    }
}
