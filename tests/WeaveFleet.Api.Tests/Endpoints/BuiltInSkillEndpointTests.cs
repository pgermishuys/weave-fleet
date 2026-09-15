using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WeaveFleet.Api.Tests.Infrastructure;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>Settings turns Fleet's built-in skills on one at a time; each is off until then.</summary>
public sealed class BuiltInSkillEndpointTests : IAsyncDisposable
{
    private readonly ApiWebApplicationFactory _factory = new(authEnabled: false);
    private readonly HttpClient _client;

    public BuiltInSkillEndpointTests()
    {
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task List_shows_every_built_in_skill_off_with_its_description()
    {
        var skills = await ListAsync();

        skills.Select(skill => skill.GetProperty("name").GetString())
            .ShouldBe(["fleet-code-review", "fleet-mockups", "fleet-run", "fleet-simplify"]);
        skills.ShouldAllBe(skill => !skill.GetProperty("enabled").GetBoolean());
        skills.ShouldAllBe(skill => skill.GetProperty("description").GetString()!.Length > 20);
    }

    [Fact]
    public async Task Turning_a_skill_on_is_kept_and_leaves_the_others_off()
    {
        var response = await _client.PutAsJsonAsync("/api/skills/built-in/fleet-run", new { enabled = true });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("name").GetString().ShouldBe("fleet-run");
        updated.GetProperty("enabled").GetBoolean().ShouldBeTrue();

        (await ListAsync())
            .Where(skill => skill.GetProperty("enabled").GetBoolean())
            .Select(skill => skill.GetProperty("name").GetString())
            .ShouldBe(["fleet-run"]);
    }

    [Fact]
    public async Task A_skill_Fleet_doesnt_ship_is_not_found()
    {
        var response = await _client.PutAsJsonAsync("/api/skills/built-in/code-review", new { enabled = true });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("BuiltInSkill with id 'code-review' was not found.");
    }

    private async Task<List<JsonElement>> ListAsync()
    {
        var response = await _client.GetAsync("/api/skills/built-in");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }
}
