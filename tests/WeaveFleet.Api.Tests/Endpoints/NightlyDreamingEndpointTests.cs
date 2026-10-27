using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Memory.Dreaming;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>"Dream each night": 404 while Dreaming is off; on, it makes the built-in automation, which people can't delete and which hides with the feature.</summary>
public sealed class NightlyDreamingEndpointTests : IDisposable
{
    private const string User = "local-user";
    private readonly ApiWebApplicationFactory _factory = new(authEnabled: false);

    public void Dispose() => _factory.Dispose();

    private async Task SetDreamingAsync(bool on)
    {
        using var scope = _factory.Services.CreateScope();
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(User))
            await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(DreamRules.PreferenceKey, on ? "true" : "false");
    }

    private static async Task<JsonElement> AutomationsAsync(HttpClient client)
        => (await client.GetFromJsonAsync<JsonElement>("/api/automations")).GetProperty("automations");

    [Fact]
    public async Task The_nightly_routes_are_a_404_while_dreaming_is_off()
    {
        var client = _factory.CreateClient();

        var get = await client.GetAsync("/api/memory/dreaming/nightly");
        var put = await client.PutAsJsonAsync("/api/memory/dreaming/nightly", new { enabled = true });

        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AutomationsAsync(client)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Turning_it_on_lists_the_built_in_automation_which_cant_be_deleted_and_hides_when_dreaming_is_turned_off()
    {
        await SetDreamingAsync(true);
        var client = _factory.CreateClient();

        var put = await client.PutAsJsonAsync("/api/memory/dreaming/nightly", new { enabled = true, timeZone = "UTC" });

        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var view = await put.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        view.GetProperty("time").GetString().ShouldBe("03:00");
        view.GetProperty("budgetTokens").GetInt32().ShouldBe(300_000);
        var id = view.GetProperty("automationId").GetString()!;
        (await client.GetFromJsonAsync<JsonElement>("/api/memory/dreaming/nightly")).GetProperty("automationId").GetString().ShouldBe(id);

        var listed = (await AutomationsAsync(client)).EnumerateArray().ShouldHaveSingleItem();
        (listed.GetProperty("id").GetString(), listed.GetProperty("builtin").GetString()).ShouldBe((id, "dreaming"));

        var delete = await client.DeleteAsync($"/api/automations/{id}");
        delete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AutomationsAsync(client)).GetArrayLength().ShouldBe(1);

        await SetDreamingAsync(false);
        (await AutomationsAsync(client)).GetArrayLength().ShouldBe(0);
    }
}
