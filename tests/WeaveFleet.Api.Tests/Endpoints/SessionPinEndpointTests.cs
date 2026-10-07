using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Pinning sessions: they sit in a Pinned group above the projects, in the order the user puts them. Sessions A to D
/// were started in that order (D newest); Sub is a subagent's hidden session in A.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class SessionPinEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string Owner = "local-user";

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: false, simulateLocalhostRequest: true);
        _client = _factory.CreateClient();
        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Pinning_puts_sessions_at_the_end_of_the_pinned_group_in_the_order_they_were_pinned()
    {
        (await PinAsync("b")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PinAsync("d")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PinAsync("a")).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PinnedAsync()).ShouldBe(["b", "d", "a"]);
        (await GetJsonAsync("/api/sessions/d")).GetProperty("pinOrder").GetDouble().ShouldBeGreaterThan(0);
        (await GetJsonAsync("/api/sessions/c")).GetProperty("pinOrder").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Pinning_before_another_pin_moves_it_there_and_answers_its_new_order()
    {
        await PinAsync("a");
        await PinAsync("b");
        await PinAsync("c");

        var response = await PinAsync("c", before: "a");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var order = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pinOrder").GetDouble();
        (await PinnedAsync()).ShouldBe(["c", "a", "b"]);
        (await GetJsonAsync("/api/sessions/c")).GetProperty("pinOrder").GetDouble().ShouldBe(order);

        await PinAsync("c", before: "b");
        (await PinnedAsync()).ShouldBe(["a", "c", "b"]);

        await PinAsync("a", before: null);
        (await PinnedAsync()).ShouldBe(["c", "b", "a"]);
    }

    [Fact]
    public async Task Dropping_into_the_same_gap_again_and_again_keeps_the_order()
    {
        await PinAsync("a");
        await PinAsync("b");

        // Each drop halves the gap before B; after ~50 it's too small and every pin is numbered again.
        for (var i = 0; i < 60; i++)
        {
            var moving = i % 2 == 0 ? "c" : "d";
            (await PinAsync(moving, before: "b")).StatusCode.ShouldBe(HttpStatusCode.OK);
            var pinned = await PinnedAsync();
            pinned[^1].ShouldBe("b");
            pinned[^2].ShouldBe(moving);
        }

        (await PinnedAsync()).ShouldBe(["a", "c", "d", "b"]);
    }

    [Fact]
    public async Task Pinned_sessions_come_first_in_the_list_so_a_page_always_has_them()
    {
        await PinAsync("a");

        var page = (await GetJsonAsync("/api/sessions?limit=2")).EnumerateArray().Select(Id).ToList();

        page.ShouldBe(["a", "d"]);
    }

    [Fact]
    public async Task Unpinning_puts_it_back_with_the_others()
    {
        await PinAsync("a");

        (await _client!.DeleteAsync("/api/sessions/a/pin")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client!.DeleteAsync("/api/sessions/a/pin")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await PinnedAsync()).ShouldBeEmpty();
        (await GetJsonAsync("/api/sessions")).EnumerateArray().Select(Id).ShouldBe(["d", "c", "b", "a"]);
    }

    [Fact]
    public async Task Archiving_a_session_unpins_it_and_restoring_it_doesnt_pin_it_again()
    {
        await PinAsync("a");
        await PinAsync("b");

        (await SetRetentionAsync("a", "archived")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PinnedAsync()).ShouldBe(["b"]);

        (await SetRetentionAsync("a", "active")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetJsonAsync("/api/sessions/a")).GetProperty("pinOrder").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_archived_session_or_a_subagents_session_cant_be_pinned()
    {
        await SetRetentionAsync("c", "archived");

        var archived = await PinAsync("c");
        archived.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await archived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("Archived sessions can't be pinned; restore it first.");

        var sub = await PinAsync("sub");
        sub.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await sub.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .ShouldBe("A subagent's session isn't in the list; pin the session it belongs to.");
    }

    [Fact]
    public async Task Pinning_or_unpinning_a_session_that_isnt_there_is_not_found()
    {
        (await PinAsync("missing")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client!.DeleteAsync("/api/sessions/missing/pin")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private Task<HttpResponseMessage> PinAsync(string id, string? before = null) =>
        _client!.PutAsJsonAsync($"/api/sessions/{id}/pin", new { beforeSessionId = before });

    private Task<HttpResponseMessage> SetRetentionAsync(string id, string retentionStatus) =>
        _client!.PatchAsJsonAsync($"/api/sessions/{id}/retention", new { retentionStatus });

    /// <summary>The pinned sessions in the list, in their pinned order.</summary>
    private async Task<List<string>> PinnedAsync() =>
        (await GetJsonAsync("/api/sessions")).EnumerateArray()
            .Where(s => s.GetProperty("pinOrder").ValueKind == JsonValueKind.Number)
            .OrderBy(s => s.GetProperty("pinOrder").GetDouble())
            .Select(Id)
            .ToList();

    private static string Id(JsonElement item) => item.GetProperty("session").GetProperty("id").GetString()!;

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client!.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SeedAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync($"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-1', '/ws', 'Work', '2026-10-01T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-1', 0, NULL, '/ws', '', 'stopped', '2026-10-01T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory, harness_type,
                lifecycle_status, retention_status, created_at, user_id, parent_session_id)
            VALUES ('a', 'ws-1', 'inst-1', 'ses_a', 'A', 'stopped', '/ws', 'opencode', 'stopped', 'active',
                    '2026-10-01T10:00:00+00:00', '{Owner}', NULL),
                   ('b', 'ws-1', 'inst-1', 'ses_b', 'B', 'stopped', '/ws', 'opencode', 'stopped', 'active',
                    '2026-10-01T10:01:00+00:00', '{Owner}', NULL),
                   ('c', 'ws-1', 'inst-1', 'ses_c', 'C', 'stopped', '/ws', 'opencode', 'stopped', 'active',
                    '2026-10-01T10:02:00+00:00', '{Owner}', NULL),
                   ('d', 'ws-1', 'inst-1', 'ses_d', 'D', 'stopped', '/ws', 'opencode', 'stopped', 'active',
                    '2026-10-01T10:03:00+00:00', '{Owner}', NULL),
                   ('sub', 'ws-1', 'inst-1', 'ses_sub', 'Sub', 'stopped', '/ws', 'opencode', 'stopped', 'active',
                    '2026-10-01T10:04:00+00:00', '{Owner}', 'a');
            """);
    }
}
