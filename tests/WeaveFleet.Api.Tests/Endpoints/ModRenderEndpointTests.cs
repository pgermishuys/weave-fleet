using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Drawing a session's tool rows through mods, and adding Fleet's sample. With no mod host running nothing is drawn,
/// so these cover the routes' shapes and checks; the draw itself is covered against a real host in the Infrastructure tests.
/// </summary>
public sealed class ModRenderEndpointTests : IClassFixture<ModRenderEndpointTests.Server>, IDisposable
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _sampleFolder;

    public ModRenderEndpointTests(Server server)
    {
        _factory = server.Factory;
        _client = _factory.CreateClient();
        var options = _factory.Services.GetRequiredService<FleetOptions>();
        var data = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        var user16 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("local-user")))[..16];
        _sampleFolder = Path.Combine(data, "mods", user16, "test-chips");
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_sampleFolder))
            Directory.Delete(_sampleFolder, recursive: true);
    }

    public sealed class Server : IDisposable
    {
        public ApiWebApplicationFactory Factory { get; } = new(authEnabled: false);
        public void Dispose() => Factory.Dispose();
    }

    private async Task TurnOnAsync()
        => (await _client.PutAsJsonAsync("/api/preferences/Mods", new { value = "true" })).EnsureSuccessStatusCode();

    private async Task<string> SeedSessionAsync()
    {
        using var scope = _factory.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        conn.Execute("""
            INSERT OR IGNORE INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-mr-test', '/tmp', 'MR Test', '2026-01-01T00:00:00Z', 'local-user')
            """);
        conn.Execute("""
            INSERT OR IGNORE INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-mr-test', 0, NULL, '/tmp', '', 'stopped', '2026-01-01T00:00:00Z', 'local-user')
            """);
        var sessionId = Guid.NewGuid().ToString();
        conn.Execute("""
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES (@Id, 'ws-mr-test', 'inst-mr-test', @OcId, 'Render Test', 'stopped', '/tmp',
                    'stopped', 'active', '2026-01-01T00:00:00Z', 'local-user')
            """,
            new { Id = sessionId, OcId = Guid.NewGuid().ToString() });
        return sessionId;
    }

    private static object Body(string site = "ToolUse", string? requestId = "call_1") => new { site, requestId, props = new { tool = "bash", output = "Passed!" } };

    [Fact]
    public async Task A_session_that_isnt_there_is_404_on_sites_and_render()
    {
        await TurnOnAsync();

        (await _client.GetAsync("/api/sessions/nope/mods/sites")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.PostAsJsonAsync("/api/sessions/nope/mods/render", Body())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sites_are_empty_while_no_mod_host_is_running()
    {
        await TurnOnAsync();
        var session = await SeedSessionAsync();

        var sites = await _client.GetFromJsonAsync<JsonElement>($"/api/sessions/{session}/mods/sites");

        sites.GetProperty("sites").GetArrayLength().ShouldBe(0);
        sites.GetProperty("panes").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_render_nothing_hooks_answers_a_null_tree_and_no_mods()
    {
        await TurnOnAsync();
        var session = await SeedSessionAsync();

        var response = await _client.PostAsJsonAsync($"/api/sessions/{session}/mods/render", Body("ToolResult"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("tree").ValueKind.ShouldBe(JsonValueKind.Null);
        view.GetProperty("mods").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("ComposerBand", "call_1", "site")]
    [InlineData("ToolUse", null, "requestId")]
    public async Task A_bad_render_request_is_400_and_names_the_field(string site, string? requestId, string field)
    {
        await TurnOnAsync();
        var session = await SeedSessionAsync();

        var response = await _client.PostAsJsonAsync($"/api/sessions/{session}/mods/render", Body(site, requestId));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!.ShouldContain($"\"{field}\"");
    }

    [Fact]
    public async Task Props_that_arent_an_object_are_400()
    {
        await TurnOnAsync();
        var session = await SeedSessionAsync();

        var response = await _client.PostAsJsonAsync($"/api/sessions/{session}/mods/render", new { site = "ToolUse", requestId = "call_1", props = "bash" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!.ShouldContain("\"props\"");
    }

    [Fact]
    public async Task An_action_for_a_session_that_isnt_there_is_404()
    {
        await TurnOnAsync();

        (await _client.PostAsJsonAsync("/api/sessions/nope/mods/action", new { handle = "h1", kind = "press" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_action_no_mod_host_can_take_answers_not_handled()
    {
        await TurnOnAsync();
        var session = await SeedSessionAsync();

        var response = await _client.PostAsJsonAsync($"/api/sessions/{session}/mods/action", new { handle = "h1", kind = "press", surface = "phone" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("handled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_bad_action_is_400_and_names_the_field()
    {
        await TurnOnAsync();
        var session = await SeedSessionAsync();

        var response = await _client.PostAsJsonAsync($"/api/sessions/{session}/mods/action", new { handle = "h1", kind = "click" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!.ShouldContain("\"kind\"");
    }

    [Fact]
    public async Task A_sample_Fleet_doesnt_ship_is_404()
    {
        await TurnOnAsync();

        (await _client.PostAsync("/api/mods/samples/no-such-sample", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_shipped_sample_is_found_and_kept_or_refused_with_the_runtimes_reason()
    {
        await TurnOnAsync();

        // Kept when a mod runtime is available; otherwise refused as Keep refuses, never 404.
        var response = await _client.PostAsync("/api/mods/samples/test-chips", null);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var mod = await response.Content.ReadFromJsonAsync<JsonElement>();
            (mod.GetProperty("name").GetString(), mod.GetProperty("active").GetInt32()).ShouldBe(("test-chips", 1));
            (await _client.PostAsync("/api/mods/samples/test-chips", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await _client.GetFromJsonAsync<JsonElement>("/api/mods/test-chips")).GetProperty("versions").GetArrayLength().ShouldBe(1);
        }
    }
}
