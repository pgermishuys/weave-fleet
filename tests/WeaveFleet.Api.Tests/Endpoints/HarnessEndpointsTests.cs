using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
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

    private static bool GetUserEnabled(IReadOnlyList<JsonElement> harnesses, string harnessType)
    {
        var harness = harnesses.Single(harness =>
            string.Equals(harness.GetProperty("type").GetString(), harnessType, StringComparison.Ordinal));

        return harness.GetProperty("userEnabled").GetBoolean();
    }
}
