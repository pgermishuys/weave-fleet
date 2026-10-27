using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class ConfigEndpointTests(ConfigEndpointTests.Server server) : IClassFixture<ConfigEndpointTests.Server>
{
    private readonly ApiWebApplicationFactory _factory = server.Factory;

    public sealed class Server : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = new(authEnabled: false);
        public void Dispose() => Factory.Dispose();
    }

    [Fact]
    public async Task get_config_returns_pooled_opencode_harness_enabled_by_default()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.GetAsync("/api/config");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>();
        payload.ShouldNotBeNull();
        payload["pooledOpenCodeHarness"].ShouldNotBeNull();
        payload["pooledOpenCodeHarness"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task put_config_toggles_pooled_opencode_harness_at_runtime()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var options = _factory.Services.GetRequiredService<FleetOptions>();
        try
        {
            var disableResponse = await client.PutAsJsonAsync("/api/config", new JsonObject
            {
                ["pooledOpenCodeHarness"] = false,
            });

            disableResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            options.Harness.PooledOpenCodeHarness.ShouldBeFalse();

            var getDisabledResponse = await client.GetAsync("/api/config");
            getDisabledResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            var disabledPayload = await getDisabledResponse.Content.ReadFromJsonAsync<JsonObject>();
            disabledPayload.ShouldNotBeNull();
            disabledPayload["pooledOpenCodeHarness"]!.GetValue<bool>().ShouldBeFalse();

            var enableResponse = await client.PutAsJsonAsync("/api/config", new JsonObject
            {
                ["pooledOpenCodeHarness"] = true,
            });

            enableResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            options.Harness.PooledOpenCodeHarness.ShouldBeTrue();
        }
        finally
        {
            // The other tests expect the default.
            options.Harness.PooledOpenCodeHarness = true;
        }
    }

    [Fact]
    public async Task put_config_rejects_non_boolean_pooled_opencode_harness_value()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var response = await client.PutAsJsonAsync("/api/config", new JsonObject
        {
            ["pooledOpenCodeHarness"] = "true",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var options = _factory.Services.GetRequiredService<FleetOptions>();
        options.Harness.PooledOpenCodeHarness.ShouldBeTrue();
    }
}
