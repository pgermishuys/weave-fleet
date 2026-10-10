using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The Mods switch's API. Runs against the real file store: each test works in its own mod name and session id, and
/// removes what it wrote from Fleet's data folder.
/// </summary>
public sealed class ModEndpointTests : IAsyncDisposable
{
    private const string TurnedOff = "Mods are turned off in Fleet's Settings.";

    private readonly ApiWebApplicationFactory _factory = new(authEnabled: false);
    private readonly HttpClient _client;
    private readonly string _name = $"test-chips-{Guid.NewGuid():N}"[..20];
    private readonly string _session = $"session-{Guid.NewGuid():N}";
    private readonly string _userFolder;

    public ModEndpointTests()
    {
        _client = _factory.CreateClient();
        var options = _factory.Services.GetRequiredService<FleetOptions>();
        var data = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        var user16 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("local-user")))[..16];
        _userFolder = Path.Combine(data, "mods", user16);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        TryDelete(Path.Combine(_userFolder, _name));
        TryDelete(Path.Combine(_userFolder, "drafts", _session));
    }

    private static void TryDelete(string folder)
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    private async Task TurnOnAsync()
        => (await _client.PutAsJsonAsync("/api/preferences/Mods", new { value = "true" })).EnsureSuccessStatusCode();

    /// <summary>Puts a draft where the agent writes it, in Fleet's data folder.</summary>
    private void WriteDraft(string? name = null, string version = "0.1.0")
    {
        name ??= _name;
        var folder = Path.Combine(_userFolder, "drafts", _session, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "mod.json"), $$"""{"name":"{{name}}","version":"{{version}}","description":"Shows chips","hooks":"mod.ts"}""");
        File.WriteAllText(Path.Combine(folder, "mod.ts"), "export default function register() {}\n");
    }

    private string Draft(string suffix = "") => $"/api/sessions/{_session}/mods/drafts/{_name}{suffix}";

    private async Task<JsonElement> KeepAsync(string? note = null)
    {
        WriteDraft();
        var response = await _client.PostAsJsonAsync(Draft("/keep"), new { note });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ListAsync()
        => await _client.GetFromJsonAsync<JsonElement>("/api/mods");

    private async Task<JsonElement> OurModAsync()
        => (await ListAsync()).GetProperty("mods").EnumerateArray().Single(m => m.GetProperty("name").GetString() == _name);

    // ── The switch ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/mods")]
    [InlineData("GET", "/api/mods/test-chips")]
    [InlineData("GET", "/api/mods/test-chips/versions/1/files")]
    [InlineData("PUT", "/api/mods/test-chips/active")]
    [InlineData("POST", "/api/mods/test-chips/undo")]
    [InlineData("POST", "/api/mods/test-chips/on")]
    [InlineData("POST", "/api/mods/test-chips/off")]
    [InlineData("PUT", "/api/mods/safe-mode")]
    [InlineData("GET", "/api/sessions/s1/mods/drafts")]
    [InlineData("GET", "/api/sessions/s1/mods/drafts/test-chips/files")]
    [InlineData("GET", "/api/sessions/s1/mods/drafts/test-chips/check")]
    [InlineData("POST", "/api/sessions/s1/mods/drafts/test-chips/keep")]
    [InlineData("POST", "/api/sessions/s1/mods/drafts/test-chips/off")]
    [InlineData("POST", "/api/sessions/s1/mods/drafts/test-chips/on")]
    public async Task Every_route_is_404_with_the_message_while_the_switch_is_off(string method, string url)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        // Binding runs before the gate, so a PUT needs the body its route reads.
        if (url.EndsWith("/active", StringComparison.Ordinal))
            request.Content = JsonContent.Create(new { version = 1 });
        else if (url.EndsWith("/safe-mode", StringComparison.Ordinal))
            request.Content = JsonContent.Create(new { on = true });

        var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe(TurnedOff);
    }

    [Fact]
    public async Task The_list_is_empty_of_our_mod_until_one_is_kept()
    {
        await TurnOnAsync();

        var list = await ListAsync();

        list.GetProperty("safeMode").GetBoolean().ShouldBeFalse();
        list.GetProperty("mods").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ShouldNotContain(_name);
    }

    // ── Drafts ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_draft_is_listed_with_its_manifest_and_nothing_kept()
    {
        await TurnOnAsync();
        WriteDraft();

        var drafts = await _client.GetFromJsonAsync<JsonElement>($"/api/sessions/{_session}/mods/drafts");

        var draft = drafts.EnumerateArray().Single();
        draft.GetProperty("sessionId").GetString().ShouldBe(_session);
        draft.GetProperty("name").GetString().ShouldBe(_name);
        draft.GetProperty("description").GetString().ShouldBe("Shows chips");
        draft.GetProperty("version").GetString().ShouldBe("0.1.0");
        draft.TryGetProperty("off", out var off).ShouldBeTrue();
        off.ValueKind.ShouldBe(JsonValueKind.Null);
        draft.TryGetProperty("kept", out var kept).ShouldBeTrue();
        kept.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_drafts_files_are_shown_as_text()
    {
        await TurnOnAsync();
        WriteDraft();

        var files = await _client.GetFromJsonAsync<JsonElement>(Draft("/files"));

        files.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ShouldBe(["mod.json", "mod.ts"]);
        files.GetProperty("files")[1].GetProperty("content").GetString().ShouldContain("register");
    }

    [Fact]
    public async Task A_draft_that_isnt_there_is_404_on_every_draft_route()
    {
        await TurnOnAsync();

        (await _client.GetAsync(Draft("/files"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.GetAsync(Draft("/check"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.PostAsync(Draft("/keep"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.PostAsync(Draft("/off"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.PostAsync(Draft("/on"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_drafts_check_answers_with_a_check_property()
    {
        await TurnOnAsync();
        WriteDraft();

        var response = await _client.GetAsync(Draft("/check"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("check", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_draft_is_turned_off_and_on()
    {
        await TurnOnAsync();
        WriteDraft();

        var off = await _client.PostAsync(Draft("/off"), null);
        off.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("off").GetProperty("by").GetString().ShouldBe("user");

        var on = await _client.PostAsync(Draft("/on"), null);
        on.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("off").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── Keep, use, undo, on and off ─────────────────────────────────────

    [Fact]
    public async Task Keep_makes_version_1_active_and_removes_the_draft()
    {
        await TurnOnAsync();

        var mod = await KeepAsync("show failing names");

        mod.GetProperty("name").GetString().ShouldBe(_name);
        mod.GetProperty("active").GetInt32().ShouldBe(1);
        mod.GetProperty("activeVersion").GetString().ShouldBe("0.1.0");
        mod.GetProperty("description").GetString().ShouldBe("Shows chips");
        mod.GetProperty("versions")[0].GetProperty("note").GetString().ShouldBe("show failing names");
        (await _client.GetFromJsonAsync<JsonElement>($"/api/sessions/{_session}/mods/drafts")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Keep_takes_no_body()
    {
        await TurnOnAsync();
        WriteDraft();

        var response = await _client.PostAsync(Draft("/keep"), null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Keep_refuses_a_note_over_2000_characters_with_400()
    {
        await TurnOnAsync();
        WriteDraft();

        var response = await _client.PostAsJsonAsync(Draft("/keep"), new { note = new string('x', 2001) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Keep_refuses_a_draft_whose_manifest_is_broken_with_400()
    {
        await TurnOnAsync();
        WriteDraft();
        File.WriteAllText(Path.Combine(_userFolder, "drafts", _session, _name, "mod.json"), "{ not json");

        var response = await _client.PostAsync(Draft("/keep"), null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_kept_mod_is_got_listed_and_its_files_read()
    {
        await TurnOnAsync();
        await KeepAsync();

        var got = await _client.GetFromJsonAsync<JsonElement>($"/api/mods/{_name}");
        got.GetProperty("active").GetInt32().ShouldBe(1);
        (await OurModAsync()).GetProperty("versions").GetArrayLength().ShouldBe(1);

        var files = await _client.GetFromJsonAsync<JsonElement>($"/api/mods/{_name}/versions/1/files");
        files.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ShouldContain("mod.ts");

        (await _client.GetAsync($"/api/mods/{_name}/versions/9/files")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_mod_nobody_kept_is_404()
    {
        await TurnOnAsync();

        (await _client.GetAsync($"/api/mods/{_name}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.PostAsync($"/api/mods/{_name}/undo", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client.PostAsync($"/api/mods/{_name}/off", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Use_undo_off_and_on_change_the_mod()
    {
        await TurnOnAsync();
        await KeepAsync();
        WriteDraft(version: "0.2.0");
        (await _client.PostAsync(Draft("/keep"), null)).EnsureSuccessStatusCode();
        (await OurModAsync()).GetProperty("active").GetInt32().ShouldBe(2);

        var undone = await _client.PostAsync($"/api/mods/{_name}/undo", null);
        undone.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await undone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("active").GetInt32().ShouldBe(1);

        var used = await _client.PutAsJsonAsync($"/api/mods/{_name}/active", new { version = 2 });
        used.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await used.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("active").GetInt32().ShouldBe(2);

        var off = await _client.PostAsync($"/api/mods/{_name}/off", null);
        (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("off").GetProperty("by").GetString().ShouldBe("user");

        var on = await _client.PostAsync($"/api/mods/{_name}/on", null);
        (await on.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("off").ValueKind.ShouldBe(JsonValueKind.Null);

        (await _client.PutAsJsonAsync($"/api/mods/{_name}/active", new { version = 9 })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Undo_on_the_first_version_turns_the_mod_off_and_then_has_nothing_to_undo()
    {
        await TurnOnAsync();
        await KeepAsync();

        var off = await _client.PostAsync($"/api/mods/{_name}/undo", null);
        off.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("off").GetProperty("by").GetString().ShouldBe("user");

        var again = await _client.PostAsync($"/api/mods/{_name}/undo", null);
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe("Nothing to undo");
    }

    [Fact]
    public async Task The_active_body_rejects_unknown_members()
    {
        await TurnOnAsync();

        var response = await _client.PutAsJsonAsync($"/api/mods/{_name}/active", new { version = 1, extra = true });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Safe mode ───────────────────────────────────────────────────────

    [Fact]
    public async Task Safe_mode_shows_in_the_list_and_the_routes_keep_working_while_it_is_set()
    {
        await TurnOnAsync();
        await KeepAsync();

        var set = await _client.PutAsJsonAsync("/api/mods/safe-mode", new { on = true });
        set.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("safeMode").GetBoolean().ShouldBeTrue();

        (await ListAsync()).GetProperty("safeMode").GetBoolean().ShouldBeTrue();
        (await _client.GetAsync($"/api/mods/{_name}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.PostAsync($"/api/mods/{_name}/off", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var cleared = await _client.PutAsJsonAsync("/api/mods/safe-mode", new { on = false });
        (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("safeMode").GetBoolean().ShouldBeFalse();
    }

    // ── Names ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/mods/..%2f..%2fescape")]
    [InlineData("/api/mods/..%5cescape")]
    [InlineData("/api/mods/Upper")]
    [InlineData("/api/mods/fleet-own")]
    [InlineData("/api/mods/drafts")]
    [InlineData("/api/mods/..%2fescape/versions/1/files")]
    [InlineData("/api/sessions/..%2f..%2fx/mods/drafts")]
    [InlineData("/api/sessions/s1/mods/drafts/..%2f..%2fescape/files")]
    [InlineData("/api/sessions/s1/mods/drafts/..%5cescape/check")]
    public async Task A_name_that_climbs_out_of_the_folder_is_404(string url)
    {
        await TurnOnAsync();

        var response = await _client.GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/api/mods/..%2fescape/undo")]
    [InlineData("/api/mods/..%2fescape/on")]
    [InlineData("/api/sessions/s1/mods/drafts/..%2fescape/keep")]
    [InlineData("/api/sessions/s1/mods/drafts/Upper/off")]
    public async Task A_name_that_climbs_out_of_the_folder_is_404_on_writes_too(string url)
    {
        await TurnOnAsync();

        var response = await _client.PostAsync(url, null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
