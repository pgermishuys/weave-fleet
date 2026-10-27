using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Skills;
using WeaveFleet.Infrastructure.Skills;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Settings turns Fleet's built-in skills on one at a time; each is off until then. One server for the class, with every
/// skill switched off and the saved versions cleared before each test; the first test checks the untouched default on its own server.
/// </summary>
public sealed class BuiltInSkillEndpointTests : IClassFixture<BuiltInSkillEndpointTests.Server>, IAsyncLifetime
{
    private readonly Server _server;
    private readonly HttpClient _client;

    public BuiltInSkillEndpointTests(Server server)
    {
        _server = server;
        _client = server.Factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        _server.ClearVersions();
        foreach (var skill in await ListAsync(_client))
        {
            if (skill.GetProperty("withMods").GetBoolean() || !skill.GetProperty("enabled").GetBoolean())
                continue;
            (await _client.PutAsJsonAsync($"/api/skills/built-in/{skill.GetProperty("name").GetString()}", new { enabled = false })).EnsureSuccessStatusCode();
        }
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    public sealed class Server : IDisposable
    {
        private readonly string _versions = Path.Combine(Path.GetTempPath(), $"fleet-api-skill-versions-{Guid.NewGuid():N}");

        public Server() => Factory = new ApiWebApplicationFactory(
            authEnabled: false,
            configureTestServices: services => services.AddSingleton<ISkillVersionStore>(new FileSkillVersionStore(_versions)));

        public ApiWebApplicationFactory Factory { get; }

        public void ClearVersions()
        {
            if (Directory.Exists(_versions))
                Directory.Delete(_versions, recursive: true);
        }

        public void Dispose()
        {
            Factory.Dispose();
            ClearVersions();
        }
    }

    [Fact]
    public async Task List_shows_every_built_in_skill_off_with_its_description()
    {
        using var fresh = new Server();
        using var client = fresh.Factory.CreateClient();

        var skills = await ListAsync(client);

        skills.Select(skill => skill.GetProperty("name").GetString())
            .ShouldBe(["fleet-code-review", "fleet-debug", "fleet-design", "fleet-diagrams", "fleet-explain", "fleet-mockups", "fleet-mod", "fleet-plan", "fleet-run", "fleet-simplify", "fleet-walkthrough"]);
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

        (await ListAsync(_client))
            .Where(skill => skill.GetProperty("enabled").GetBoolean())
            .Select(skill => skill.GetProperty("name").GetString())
            .ShouldBe(["fleet-run"]);
    }

    [Fact]
    public async Task Fleet_mod_has_no_switch_because_it_comes_with_Mods()
    {
        (await ListAsync(_client)).Single(skill => skill.GetProperty("name").GetString() == "fleet-mod")
            .GetProperty("withMods").GetBoolean().ShouldBeTrue();

        var response = await _client.PutAsJsonAsync("/api/skills/built-in/fleet-mod", new { enabled = true });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!.ShouldContain("Mods");
    }

    [Fact]
    public async Task A_skill_Fleet_doesnt_ship_is_not_found()
    {
        var response = await _client.PutAsJsonAsync("/api/skills/built-in/code-review", new { enabled = true });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("BuiltInSkill with id 'code-review' was not found.");
    }

    [Fact]
    public async Task A_skill_shows_Fleets_text_until_the_user_saves_a_version_of_their_own()
    {
        var fleet = await GetAsync("fleet-code-review");
        fleet.GetProperty("fleetContent").GetString()!.ShouldStartWith("---\nname: fleet-code-review\n");
        fleet.GetProperty("yourContent").ValueKind.ShouldBe(JsonValueKind.Null);
        fleet.GetProperty("versions").GetArrayLength().ShouldBe(0);

        var mine = fleet.GetProperty("fleetContent").GetString() + "\nNaming and style aren't findings.\n";
        var response = await _client.PostAsJsonAsync("/api/skills/built-in/fleet-code-review/versions", new { content = mine, note = "No style nits." });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        saved.GetProperty("version").GetInt32().ShouldBe(1);
        saved.GetProperty("yourContent").GetString().ShouldBe(mine);
        saved.GetProperty("versions")[0].GetProperty("note").GetString().ShouldBe("No style nits.");
        (await ListAsync(_client)).First().GetProperty("version").GetInt32().ShouldBe(1);

        var version = await _client.GetFromJsonAsync<JsonElement>("/api/skills/built-in/fleet-code-review/versions/1");
        version.GetProperty("content").GetString().ShouldBe(mine);
    }

    [Fact]
    public async Task Going_back_to_Fleets_version_keeps_the_users_versions()
    {
        var fleet = (await GetAsync("fleet-run")).GetProperty("fleetContent").GetString()!;
        await _client.PostAsJsonAsync("/api/skills/built-in/fleet-run/versions", new { content = fleet + "\nMine.\n" });

        var response = await _client.PutAsJsonAsync("/api/skills/built-in/fleet-run/active", new { version = (int?)null });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var back = await response.Content.ReadFromJsonAsync<JsonElement>();
        back.GetProperty("version").ValueKind.ShouldBe(JsonValueKind.Null);
        back.GetProperty("versions").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_version_that_renames_the_skill_is_refused_with_why()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/skills/built-in/fleet-run/versions",
            new { content = "---\nname: my-run\ndescription: Mine.\n---\n" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("Keep the name in the front matter as fleet-run: it's how sessions find the skill.");
    }

    private async Task<JsonElement> GetAsync(string name)
    {
        var response = await _client.GetAsync($"/api/skills/built-in/{name}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/skills/built-in");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }
}
