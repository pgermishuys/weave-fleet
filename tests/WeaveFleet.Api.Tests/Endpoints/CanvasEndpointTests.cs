using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Events;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The user's canvas endpoints: list a session's open canvases and close one. Canvases are seeded
/// straight into the DB for the authenticated test user (sub=test-user) and for other-user.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class CanvasEndpointTests : IClassFixture<CanvasEndpointTests.Server>, IAsyncLifetime
#pragma warning restore CA1001
{
    private const string ShopState = """{"url":"http://localhost:5173/","appId":"app_1"}""";
    private const string AdminState = """{"url":"http://localhost:3000/admin"}""";
    // A retired kind: a diagram saved before diagrams went away.
    private const string DiagramState = """{"direction":"LR","nodes":[{"id":"n1","label":"NuCode session"}],"edges":[]}""";

    private readonly ApiWebApplicationFactory _factory;
    private HttpClient? _client;
    private string? _csrfToken;

    public CanvasEndpointTests(Server server)
    {
        _factory = server.Factory;
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        _csrfToken = await GetCsrfTokenAsync(_client);

        // The server is shared: put the canvases back as they were, as on a fresh one.
        using var scope = _factory.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();

        await connection.ExecuteAsync("DELETE FROM canvas_revisions; DELETE FROM canvases;");

        await InsertCanvasAsync(connection, "cv_diagram", "sess-diagram", "test-user", "diagram", "Session event flow", DiagramState, version: 2, createdAt: "2026-09-12T10:00:00.0000000Z");
        await InsertCanvasAsync(connection, "cv_shop", "sess-mine", "test-user", "browser", "Shop", ShopState, version: 2, createdAt: "2026-09-12T10:00:00.0000000Z");
        await InsertCanvasAsync(connection, "cv_admin", "sess-mine", "test-user", "browser", "Admin", AdminState, version: 1, createdAt: "2026-09-12T10:05:00.0000000Z");
        await InsertCanvasAsync(connection, "cv_closed", "sess-mine", "test-user", "browser", "Old tab", AdminState, version: 1, createdAt: "2026-09-12T09:00:00.0000000Z", closedAt: "2026-09-12T09:30:00.0000000Z");
        await InsertCanvasAsync(connection, "cv_mine_2", "sess-mine-2", "test-user", "browser", "Other session", AdminState, version: 1, createdAt: "2026-09-12T10:00:00.0000000Z");
        await InsertCanvasAsync(connection, "cv_other", "sess-other", "other-user", "browser", "Theirs", AdminState, version: 1, createdAt: "2026-09-12T10:00:00.0000000Z");
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task List_returns_the_open_canvases_oldest_first_with_their_full_state()
    {
        var response = await _client!.GetAsync("/api/sessions/sess-mine/canvases");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(
            $$"""[{"canvasId":"cv_shop","kind":"browser","title":"Shop","version":2,"state":{{ShopState}}},{"canvasId":"cv_admin","kind":"browser","title":"Admin","version":1,"state":{{AdminState}}}]""");
    }

    [Fact]
    public async Task List_leaves_out_a_canvas_of_a_retired_kind_that_is_still_saved()
    {
        var response = await _client!.GetAsync("/api/sessions/sess-diagram/canvases");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("[]");

        using var scope = _factory.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM canvases WHERE id = 'cv_diagram'")).ShouldBe(1);
    }

    [Fact]
    public async Task Focus_reopens_a_closed_canvas_and_returns_it()
    {
        var response = await FocusAsync("sess-mine", "cv_closed");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web);
        body.GetProperty("canvasId").GetString().ShouldBe("cv_closed");
        (await ListCanvasIdsAsync("sess-mine")).ShouldBe(["cv_closed", "cv_shop", "cv_admin"]);
    }

    [Fact]
    public async Task Focus_returns_not_found_for_an_unknown_canvas_or_another_users_session()
    {
        (await FocusAsync("sess-mine", "cv_missing")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FocusAsync("sess-mine", "cv_mine_2")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FocusAsync("sess-other", "cv_other")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task List_returns_an_empty_array_for_a_session_without_canvases()
    {
        await CloseAsync("sess-mine-2", "cv_mine_2");

        var response = await _client!.GetAsync("/api/sessions/sess-mine-2/canvases");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("[]");
    }

    [Fact]
    public async Task Close_removes_the_canvas_from_the_list_and_sends_canvas_closed()
    {
        var broadcaster = _factory.Services.GetRequiredService<IEventBroadcaster>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = broadcaster.SubscribeAsync(["session:sess-mine"], "test-user", cts.Token).GetAsyncEnumerator(cts.Token);
        // The subscription registers when the enumerator first runs, so start it before the request.
        var next = events.MoveNextAsync().AsTask();

        var response = await CloseAsync("sess-mine", "cv_shop");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await next).ShouldBeTrue();
        events.Current.Type.ShouldBe("canvas.closed");
        events.Current.Payload.GetRawText().ShouldBe("""{"sessionId":"sess-mine","canvasId":"cv_shop"}""");

        var canvases = await ListCanvasIdsAsync("sess-mine");
        canvases.ShouldBe(["cv_admin"]);
    }

    [Fact]
    public async Task Close_is_idempotent()
    {
        (await CloseAsync("sess-mine", "cv_closed")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await CloseAsync("sess-mine", "cv_closed")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Close_returns_not_found_for_an_unknown_canvas_or_one_from_another_session()
    {
        (await CloseAsync("sess-mine", "cv_missing")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CloseAsync("sess-mine", "cv_mine_2")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ListCanvasIdsAsync("sess-mine-2")).ShouldBe(["cv_mine_2"]);
    }

    [Fact]
    public async Task Another_users_session_and_canvases_return_not_found()
    {
        (await _client!.GetAsync("/api/sessions/sess-other/canvases")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _client!.GetAsync("/api/sessions/sess-missing/canvases")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CloseAsync("sess-other", "cv_other")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using var scope = _factory.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var closedAt = await connection.ExecuteScalarAsync<string?>("SELECT closed_at FROM canvases WHERE id = 'cv_other'");
        closedAt.ShouldBeNull();
    }

    public sealed class Server : IAsyncLifetime
    {
        public ApiWebApplicationFactory Factory { get; } = new(authEnabled: true, useTestAuthentication: true);

        public async Task InitializeAsync()
        {
            using var scope = Factory.Services.CreateScope();
            using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();

            await connection.ExecuteAsync("""
                INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
                VALUES ('ws-mine', '/ws-mine', 'Mine', '2026-09-01T00:00:00+00:00', 'test-user'),
                       ('ws-other', '/ws-other', 'Other', '2026-09-01T00:00:00+00:00', 'other-user');
                INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
                VALUES ('inst-mine', 0, NULL, '/ws-mine', '', 'stopped', '2026-09-01T00:00:00+00:00', 'test-user'),
                       ('inst-other', 0, NULL, '/ws-other', '', 'stopped', '2026-09-01T00:00:00+00:00', 'other-user');
                INSERT INTO sessions (
                    id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                    lifecycle_status, retention_status, created_at, user_id)
                VALUES ('sess-mine', 'ws-mine', 'inst-mine', 'oc-mine', 'Mine', 'stopped', '/ws-mine',
                        'stopped', 'active', '2026-09-01T10:00:00+00:00', 'test-user'),
                       ('sess-mine-2', 'ws-mine', 'inst-mine', 'oc-mine-2', 'Mine too', 'stopped', '/ws-mine',
                        'stopped', 'active', '2026-09-01T11:00:00+00:00', 'test-user'),
                       ('sess-diagram', 'ws-mine', 'inst-mine', 'oc-diagram', 'Has a diagram', 'stopped', '/ws-mine',
                        'stopped', 'active', '2026-09-01T11:30:00+00:00', 'test-user'),
                       ('sess-other', 'ws-other', 'inst-other', 'oc-other', 'Other', 'stopped', '/ws-other',
                        'stopped', 'active', '2026-09-01T12:00:00+00:00', 'other-user');
                """);
        }

        public async Task DisposeAsync() => await Factory.DisposeAsync();
    }

    private async Task<HttpResponseMessage> CloseAsync(string sessionId, string canvasId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/sessions/{sessionId}/canvases/{canvasId}");
        request.Headers.Add("X-CSRF-Token", _csrfToken);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> FocusAsync(string sessionId, string canvasId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/canvases/{canvasId}/focus");
        request.Headers.Add("X-CSRF-Token", _csrfToken);
        return await _client!.SendAsync(request);
    }

    private async Task<string[]> ListCanvasIdsAsync(string sessionId)
    {
        var response = await _client!.GetAsync($"/api/sessions/{sessionId}/canvases");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var canvases = await response.Content.ReadFromJsonAsync<JsonElement[]>(JsonSerializerOptions.Web);
        return canvases.ShouldNotBeNull().Select(canvas => canvas.GetProperty("canvasId").GetString()!).ToArray();
    }

    private static Task<int> InsertCanvasAsync(
        IDbConnection connection,
        string id,
        string sessionId,
        string userId,
        string kind,
        string title,
        string stateJson,
        int version,
        string createdAt,
        string? closedAt = null)
        => connection.ExecuteAsync(
            """
            INSERT INTO canvases (id, session_id, user_id, kind, title, state_json, version, agent_seen_version, created_at, updated_at, closed_at)
            VALUES (@Id, @SessionId, @UserId, @Kind, @Title, @StateJson, @Version, @Version, @CreatedAt, @CreatedAt, @ClosedAt)
            """,
            new
            {
                Id = id,
                SessionId = sessionId,
                UserId = userId,
                Kind = kind,
                Title = title,
                StateJson = stateJson,
                Version = version,
                CreatedAt = createdAt,
                ClosedAt = closedAt,
            });

    private static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/user/me");
        response.EnsureSuccessStatusCode();

        var csrfToken = response.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? ExtractCookieValue(setCookies, ".WeaveFleet.CSRF")
            : null;

        csrfToken.ShouldNotBeNull();
        return csrfToken;
    }

    private static string? ExtractCookieValue(IEnumerable<string> setCookies, string cookieName)
    {
        foreach (var header in setCookies)
        {
            if (!header.StartsWith(cookieName + "=", StringComparison.Ordinal))
                continue;

            var endIndex = header.IndexOf(';', StringComparison.Ordinal);
            return endIndex >= 0
                ? header.Substring(cookieName.Length + 1, endIndex - cookieName.Length - 1)
                : header[(cookieName.Length + 1)..];
        }

        return null;
    }
}
