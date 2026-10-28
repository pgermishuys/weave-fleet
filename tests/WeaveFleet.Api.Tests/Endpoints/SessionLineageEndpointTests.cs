using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Lineage the user and agents change: moving a fork or a started session out of the session it came from (and back),
/// and the depth past which an agent can't start sessions. The chain: the user started Root; its agent started One, whose
/// agent started Two, whose agent started Three. Fork is a fork of Root, Sub a subagent's hidden session in Three.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class SessionLineageEndpointTests : IClassFixture<SessionLineageEndpointTests.Server>, IAsyncLifetime
#pragma warning restore CA1001
{
    private const string Owner = "local-user";
    private const string Token = "token-1";

    private readonly ApiWebApplicationFactory _factory;
    private readonly string _folder;
    private HttpClient? _client;

    public SessionLineageEndpointTests(Server server)
    {
        _factory = server.Factory;
        _folder = server.Folder;
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        // The server is shared: undo any move from an earlier test, as on a fresh one.
        using var scope = _factory.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync("UPDATE sessions SET lineage_detached_at = NULL");
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_agent_two_levels_down_can_still_start_a_session()
    {
        var response = await StartFromAgentAsync("ses_two");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var session = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session");
        session.GetProperty("spawnedBySessionId").GetString().ShouldBe("two");
        // It goes where the session that started it is, not to Scratch.
        session.GetProperty("projectId").GetString().ShouldBe("proj-1");
    }

    [Theory]
    [InlineData("ses_three")]
    [InlineData("ses_sub")]   // a subagent is part of its parent's turn: as deep as Three
    public async Task An_agent_three_levels_down_is_told_it_cant_go_deeper(string harnessSession)
    {
        var before = (await ListAsync()).Count(IsGoDeeper);

        var response = await StartFromAgentAsync(harnessSession);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        error.ShouldBe("This session is 3 levels down from a session the user started; Fleet doesn't let agents start "
            + "sessions deeper than that. Ask the user, or do the work here.");
        (await ListAsync()).Count(IsGoDeeper).ShouldBe(before);
    }

    [Fact]
    public async Task A_session_moved_out_of_its_parent_counts_as_the_users_and_its_agent_can_start_sessions_again()
    {
        (await PatchLineageAsync("two", detached: true)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await StartFromAgentAsync("ses_three");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Moving_a_fork_out_keeps_where_it_came_from_and_moving_it_back_clears_the_mark()
    {
        (await PatchLineageAsync("fork", detached: true)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var detached = await GetJsonAsync("/api/sessions/fork");
        detached.GetProperty("forkedFromSessionId").GetString().ShouldBe("root");
        detached.GetProperty("lineageDetachedAt").GetString().ShouldNotBeNullOrEmpty();
        var listed = (await ListAsync()).Single(s => s.GetProperty("session").GetProperty("id").GetString() == "fork");
        listed.GetProperty("lineageDetachedAt").GetString().ShouldBe(detached.GetProperty("lineageDetachedAt").GetString());
        listed.GetProperty("forkedFromSessionId").GetString().ShouldBe("root");

        (await PatchLineageAsync("fork", detached: false)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var back = await GetJsonAsync("/api/sessions/fork");
        back.GetProperty("lineageDetachedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        back.GetProperty("forkedFromSessionId").GetString().ShouldBe("root");
    }

    [Fact]
    public async Task Moving_it_out_twice_keeps_when_it_first_was()
    {
        await PatchLineageAsync("one", detached: true);
        var first = (await GetJsonAsync("/api/sessions/one")).GetProperty("lineageDetachedAt").GetString();

        (await PatchLineageAsync("one", detached: true)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await GetJsonAsync("/api/sessions/one")).GetProperty("lineageDetachedAt").GetString().ShouldBe(first);
    }

    [Theory]
    [InlineData("sub", "A subagent's session belongs to its parent's turn; it can't be moved out.")]
    [InlineData("root", "This session didn't come from another session.")]
    public async Task A_subagents_session_or_one_the_user_started_cant_be_moved_out(string id, string message)
    {
        var response = await PatchLineageAsync(id, detached: true);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe(message);
    }

    [Fact]
    public async Task Moving_a_session_that_isnt_there_is_not_found()
    {
        (await PatchLineageAsync("missing", detached: true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static bool IsGoDeeper(JsonElement s) => s.GetProperty("session").GetProperty("title").GetString() == "Go deeper";

    private Task<HttpResponseMessage> PatchLineageAsync(string id, bool detached) =>
        _client!.PatchAsJsonAsync($"/api/sessions/{id}/lineage", new { detached });

    private async Task<HttpResponseMessage> StartFromAgentAsync(string harnessSession)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/agent/{Token}/api/sessions")
        {
            Content = JsonContent.Create(new { directory = _folder, title = "Go deeper", harnessType = "opencode2" }),
        };
        request.Headers.Add("X-Fleet-Harness-Session", harnessSession);
        return await _client!.SendAsync(request);
    }

    private async Task<IEnumerable<JsonElement>> ListAsync() => (await GetJsonAsync("/api/sessions")).EnumerateArray().ToList();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await _client!.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public sealed class Server : IAsyncLifetime
    {
        private readonly List<IHarnessSession> _spawned = [];

        public string Folder { get; } = Directory.CreateTempSubdirectory("fleet-lineage-tests-").FullName;
        public ApiWebApplicationFactory Factory { get; }

        public Server()
        {
            var registry = new FakeHarnessRegistry();
            registry.Register(new FakeHarness("opencode2", "OpenCode 2"));
            registry.Register(new FakeHarnessRuntime("opencode2")
            {
                SpawnBehavior = (_, _) =>
                {
                    var session = new FakeHarnessSession($"inst-spawned-{Guid.NewGuid():N}");
                    lock (_spawned) _spawned.Add(session);
                    return Task.FromResult<IHarnessSession>(session);
                },
            });
            Factory = new ApiWebApplicationFactory(
                authEnabled: false,
                simulateLocalhostRequest: true,
                configureTestServices: services =>
                {
                    var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IHarnessRegistry));
                    if (existing is not null) services.Remove(existing);
                    services.AddSingleton<IHarnessRegistry>(registry);
                    services.AddSingleton<IHarnessCanvasCallerResolver>(new FakeCallers());
                    services.AddSingleton<IHarnessBridgeTokens>(new FakeTokens());
                });
        }

        public Task InitializeAsync() => SeedAsync(Factory, Folder);

        public async Task DisposeAsync()
        {
            await Factory.DisposeAsync();
            foreach (var session in _spawned)
                await session.DisposeAsync();
            Directory.Delete(Folder, recursive: true);
        }

        private static async Task SeedAsync(ApiWebApplicationFactory factory, string folder)
        {
            using var scope = factory.Services.CreateScope();
            using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await connection.ExecuteAsync(
                "INSERT INTO workspace_roots (id, path, user_id) VALUES ('root-1', @Path, @Owner)",
                new { Path = folder, Owner });
            await connection.ExecuteAsync($"""
                INSERT INTO projects (id, name, type, position, created_at, updated_at, user_id)
                VALUES ('proj-1', 'Weave', 'user', 1, '2026-10-01', '2026-10-01', '{Owner}');
                INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
                VALUES ('ws-1', '/ws', 'Work', '2026-10-01T00:00:00+00:00', '{Owner}');
                INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
                VALUES ('inst-1', 0, NULL, '/ws', '', 'running', '2026-10-01T00:00:00+00:00', '{Owner}');
                INSERT INTO sessions (
                    id, workspace_id, instance_id, opencode_session_id, title, status, directory, harness_type,
                    lifecycle_status, retention_status, created_at, user_id, parent_session_id, forked_from_session_id,
                    spawned_by_session_id, spawn_kind)
                VALUES ('root', 'ws-1', 'inst-1', 'ses_root', 'Root', 'active', '/ws', 'opencode2', 'running', 'active',
                        '2026-10-01T10:00:00+00:00', '{Owner}', NULL, NULL, NULL, NULL),
                       ('one', 'ws-1', 'inst-1', 'ses_one', 'One', 'active', '/ws', 'opencode2', 'running', 'active',
                        '2026-10-01T10:01:00+00:00', '{Owner}', NULL, NULL, 'root', 'api'),
                       ('two', 'ws-1', 'inst-1', 'ses_two', 'Two', 'active', '/ws', 'opencode2', 'running', 'active',
                        '2026-10-01T10:02:00+00:00', '{Owner}', NULL, NULL, 'one', 'api'),
                       ('three', 'ws-1', 'inst-1', 'ses_three', 'Three', 'active', '/ws', 'opencode2', 'running', 'active',
                        '2026-10-01T10:03:00+00:00', '{Owner}', NULL, NULL, 'two', 'api'),
                       ('fork', 'ws-1', 'inst-1', 'ses_fork', 'Fork', 'active', '/ws', 'opencode2', 'running', 'active',
                        '2026-10-01T10:04:00+00:00', '{Owner}', NULL, 'root', NULL, 'fork'),
                       ('sub', 'ws-1', 'inst-1', 'ses_sub', 'Sub', 'active', '/ws', 'opencode2', 'running', 'active',
                        '2026-10-01T10:05:00+00:00', '{Owner}', 'three', NULL, NULL, NULL);
                UPDATE sessions SET project_id = 'proj-1' WHERE id = 'two';
                """);
        }
    }

    private sealed class FakeTokens : IHarnessBridgeTokens
    {
        public bool IsKnown(string bridgeToken) => bridgeToken == Token;
    }

    /// <summary>Each session's agent names its own harness session: <c>ses_&lt;id&gt;</c>.</summary>
    private sealed class FakeCallers : IHarnessCanvasCallerResolver
    {
        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(bridgeToken == Token && harnessSessionId.StartsWith("ses_", StringComparison.Ordinal)
                ? new HarnessCanvasCaller(harnessSessionId["ses_".Length..], Owner)
                : null);
    }
}
