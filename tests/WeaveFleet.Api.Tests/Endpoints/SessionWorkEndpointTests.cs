using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Running work over HTTP: a session's work, stopping one piece and reading its output, everyone's running work for the
/// status bar, and what the session list says about it. And lineage: a session an agent starts through the API knows
/// which session started it, when the agent names itself.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class SessionWorkEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string Owner = "local-user";
    private const string Token = "token-1";
    private const string Parent = "sess-parent";
    private const string Quiet = "sess-quiet";

    private readonly FakeHarnessSession _harness = new("inst-1");
    private readonly FakeHarnessSession _spawned = new("inst-spawned");
    private readonly string _folder = Directory.CreateTempSubdirectory("fleet-work-tests-").FullName;
    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var registry = new FakeHarnessRegistry();
        registry.Register(new FakeHarness("opencode2", "OpenCode 2"));
        registry.Register(new FakeHarnessRuntime("opencode2") { DefaultSession = _spawned });
        _factory = new ApiWebApplicationFactory(
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
        _client = _factory.CreateClient();
        await SeedAsync();
        _factory.Services.GetRequiredService<InstanceTracker>().Register("inst-1", _harness);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _harness.DisposeAsync();
        await _spawned.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task A_sessions_work_is_what_runs_and_what_just_ended_and_everything_with_all()
    {
        var work = await GetJsonAsync($"/api/sessions/{Parent}/work");
        var all = await GetJsonAsync($"/api/sessions/{Parent}/work?all=true");

        work.EnumerateArray().Select(w => w.GetProperty("workId").GetString()).ShouldBe(["sh_dev", "sh_lint"]);
        all.EnumerateArray().Select(w => w.GetProperty("workId").GetString()).ShouldBe(["call_old", "sh_dev", "sh_lint"]);

        var dev = work[0];
        dev.GetProperty("id").GetString().ShouldBe("w-dev");
        dev.GetProperty("sessionId").GetString().ShouldBe(Parent);
        dev.GetProperty("kind").GetString().ShouldBe("shell");
        dev.GetProperty("label").GetString().ShouldBe("bun run dev");
        dev.GetProperty("status").GetString().ShouldBe("running");
        dev.GetProperty("canStop").GetBoolean().ShouldBeTrue();
        dev.GetProperty("canReadOutput").GetBoolean().ShouldBeTrue();
        dev.GetProperty("endedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        var lint = work[1];
        lint.GetProperty("endedReason").GetString().ShouldBe("completed");
        lint.GetProperty("detail").GetString().ShouldBe("exit 0");
    }

    [Fact]
    public async Task The_work_of_a_session_that_isnt_there_is_not_found()
    {
        (await _client!.GetAsync("/api/sessions/sess-missing/work")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Stopping_work_asks_the_harness_and_answers_with_the_work_ended()
    {
        _harness.StopWorkBehavior = _ => true;

        var response = await _client!.PostAsync($"/api/sessions/{Parent}/work/w-dev/stop", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _harness.StopWorkCalls.ShouldBe(["sh_dev"]);
        var stopped = await response.Content.ReadFromJsonAsync<JsonElement>();
        stopped.GetProperty("status").GetString().ShouldBe("cancelled");
        stopped.GetProperty("endedReason").GetString().ShouldBe("cancelled");
        (await GetJsonAsync("/api/work/running")).EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Stopping_work_that_finished_is_a_conflict_and_unknown_work_is_not_found()
    {
        _harness.StopWorkBehavior = _ => true;

        (await _client!.PostAsync($"/api/sessions/{Parent}/work/w-lint/stop", content: null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _client!.PostAsync($"/api/sessions/{Parent}/work/w-nope/stop", content: null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _harness.StopWorkCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Output_is_a_page_from_the_offset_asked_for()
    {
        _harness.WorkOutputBehavior = (_, offset) => new WorkOutput("ready on :5173\n", offset + 15, 40, Truncated: false);

        var page = await GetJsonAsync($"/api/sessions/{Parent}/work/w-dev/output?offset=25");

        page.GetProperty("output").GetString().ShouldBe("ready on :5173\n");
        page.GetProperty("nextOffset").GetInt64().ShouldBe(40);
        page.GetProperty("size").GetInt64().ShouldBe(40);
        page.GetProperty("truncated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Everyones_running_work_is_listed_for_the_status_bar()
    {
        var running = await GetJsonAsync("/api/work/running");

        running.EnumerateArray().Select(w => (w.GetProperty("sessionId").GetString(), w.GetProperty("workId").GetString()))
            .ShouldBe([(Parent, "sh_dev")]);
    }

    [Fact]
    public async Task The_session_list_counts_running_work_and_says_where_a_session_came_from()
    {
        var sessions = await GetJsonAsync("/api/sessions");

        var parent = sessions.EnumerateArray().Single(s => s.GetProperty("session").GetProperty("id").GetString() == Parent);
        parent.GetProperty("runningWorkCount").GetInt32().ShouldBe(1);
        var quiet = sessions.EnumerateArray().Single(s => s.GetProperty("session").GetProperty("id").GetString() == Quiet);
        quiet.GetProperty("runningWorkCount").GetInt32().ShouldBe(0);
        quiet.GetProperty("forkedFromSessionId").GetString().ShouldBe(Parent);
        quiet.GetProperty("spawnKind").GetString().ShouldBe("fork");

        var single = await GetJsonAsync($"/api/sessions/{Quiet}");
        single.GetProperty("forkedFromSessionId").GetString().ShouldBe(Parent);
        single.GetProperty("spawnKind").GetString().ShouldBe("fork");
    }

    [Fact]
    public async Task A_session_an_agent_starts_naming_its_own_session_remembers_who_started_it()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/agent/{Token}/api/sessions")
        {
            Content = JsonContent.Create(new { directory = _folder, title = "Fix Pi model switch", harnessType = "opencode2" }),
        };
        request.Headers.Add("X-Fleet-Harness-Session", "ses_parent");

        var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var session = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session");
        session.GetProperty("spawnedBySessionId").GetString().ShouldBe(Parent);
        session.GetProperty("spawnKind").GetString().ShouldBe("api");
        var listed = await GetJsonAsync($"/api/sessions/{session.GetProperty("id").GetString()}");
        listed.GetProperty("spawnedBySessionId").GetString().ShouldBe(Parent);
    }

    [Theory]
    [InlineData("/api/sessions", "ses_parent")]   // not an agent's request: anyone could send the header
    [InlineData("/agent/token-1/api/sessions", "ses_someone_else")]   // the process doesn't run that session
    [InlineData("/agent/token-1/api/sessions", null)]
    public async Task A_session_started_by_anyone_else_has_no_starter(string path, string? harnessSession)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { directory = _folder, title = "Mine", harnessType = "opencode2" }),
        };
        if (harnessSession is not null)
            request.Headers.Add("X-Fleet-Harness-Session", harnessSession);

        var response = await _client!.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var session = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session");
        (session.TryGetProperty("spawnedBySessionId", out var spawnedBy) && spawnedBy.ValueKind == JsonValueKind.String).ShouldBeFalse();
    }

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
        var now = DateTime.UtcNow;
        var old = now.AddHours(-2).ToString("O");
        var started = now.AddMinutes(-3).ToString("O");
        var ended = now.AddMinutes(-1).ToString("O");
        await connection.ExecuteAsync(
            "INSERT INTO workspace_roots (id, path, user_id) VALUES ('root-1', @Path, @Owner)",
            new { Path = _folder, Owner });
        await connection.ExecuteAsync($"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-1', '/ws', 'Work', '2026-10-01T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-1', 0, NULL, '/ws', '', 'running', '2026-10-01T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory, harness_type,
                lifecycle_status, retention_status, created_at, user_id, forked_from_session_id, spawn_kind)
            VALUES ('{Parent}', 'ws-1', 'inst-1', 'ses_parent', 'Capture subagents', 'active', '/ws', 'opencode2',
                    'running', 'active', '2026-10-01T10:00:00+00:00', '{Owner}', NULL, NULL),
                   ('{Quiet}', 'ws-1', 'inst-1', 'ses_quiet', 'Fork: emit jobs', 'active', '/ws', 'opencode2',
                    'running', 'active', '2026-10-01T11:00:00+00:00', '{Owner}', '{Parent}', 'fork');
            INSERT INTO delegations (
                id, parent_session_id, parent_tool_call_id, title, status, created_at, updated_at, completed_at,
                kind, work_id, label, background, can_stop, can_read_output, ended_reason, detail)
            VALUES ('w-old', '{Parent}', 'call_old', 'explore', 'completed', '{old}', '{old}', '{old}',
                    'subagent', 'call_old', 'Find the mapper', 0, 0, 0, 'completed', NULL),
                   ('w-dev', '{Parent}', 'call_dev', 'shell', 'running', '{started}', '{started}', NULL,
                    'shell', 'sh_dev', 'bun run dev', 1, 1, 1, NULL, NULL),
                   ('w-lint', '{Parent}', 'call_lint', 'shell', 'completed', '{started}', '{ended}', '{ended}',
                    'shell', 'sh_lint', 'bun run lint', 1, 1, 1, 'completed', 'exit 0');
            """);
    }

    private sealed class FakeTokens : IHarnessBridgeTokens
    {
        public bool IsKnown(string bridgeToken) => bridgeToken == Token;
    }

    private sealed class FakeCallers : IHarnessCanvasCallerResolver
    {
        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(bridgeToken == Token && harnessSessionId == "ses_parent"
                ? new HarnessCanvasCaller(Parent, Owner)
                : null);
    }
}
