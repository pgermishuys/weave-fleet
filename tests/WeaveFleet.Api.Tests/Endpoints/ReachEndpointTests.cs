using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>Only the desktop app's own Fleet has listeners to open: any other Fleet says so and refuses to change.</summary>
public sealed class ReachEndpointTests : IDisposable
{
    private readonly ApiWebApplicationFactory _factory = new(
        authEnabled: false,
        tokenAuthEnabled: true,
        simulateLocalhostRequest: true,
        configureTestServices: _ => { });

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Should_say_a_fleet_that_is_not_the_desktop_app_cannot_be_changed()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/machine/reach");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("available").GetBoolean().ShouldBeFalse();
        body.GetProperty("mode").GetString().ShouldBe("off");
        body.GetProperty("listening").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Should_refuse_to_open_listeners_on_a_fleet_that_is_not_the_desktop_app()
    {
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/machine/reach", new { mode = "tailscale-and-home" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
