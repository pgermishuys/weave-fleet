using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>Settings → Memory with "Search older notes" on: the overview says so, and a note moves between the two groups.</summary>
public sealed class MemoryPlacementEndpointTests : IDisposable
{
    private const string User = "local-user";
    private readonly ApiWebApplicationFactory _factory = new(authEnabled: false);
    private readonly string _repository = Directory.CreateTempSubdirectory("fleet-placement-").FullName;

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_repository, recursive: true);
    }

    [Fact]
    public async Task A_note_moves_to_search_and_is_pinned_back_and_a_bad_group_is_refused()
    {
        using (var scope = _factory.Services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(User))
        {
            var preferences = scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>();
            await preferences.SetAsync(AgentMemory.PreferenceKey, "true");
            await preferences.SetAsync(AgentMemory.SearchPreferenceKey, "true");
        }

        var client = _factory.CreateClient();
        (await client.GetFromJsonAsync<JsonElement>("/api/memory")).GetProperty("searchEnabled").GetBoolean().ShouldBeTrue();
        var added = await (await client.PostAsJsonAsync("/api/memory/notes", new { list = "repository", repository = _repository, text = "Cut releases from main." }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = added.GetProperty("id").GetString()!;

        var down = await client.PostAsJsonAsync($"/api/memory/notes/{id}/place", new { group = "search" });
        down.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await down.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("inSearch").GetBoolean().ShouldBeTrue();

        var up = await (await client.PostAsJsonAsync($"/api/memory/notes/{id}/place", new { group = "always" })).Content.ReadFromJsonAsync<JsonElement>();
        up.GetProperty("inSearch").GetBoolean().ShouldBeFalse();
        up.GetProperty("pinned").GetBoolean().ShouldBeTrue();

        (await client.PostAsJsonAsync($"/api/memory/notes/{id}/place", new { group = "sideways" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/memory/notes/00000000/place", new { group = "search" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
