using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// The user's canvas endpoints: list a session's open canvases and close one. Canvases are seeded
/// straight into the DB for the authenticated test user (sub=test-user) and for other-user.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class CanvasEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string DiagramState = """{"direction":"LR","nodes":[{"id":"n1","label":"NuCode session","detail":"NuCode/Sessions","x":20,"y":44.5,"placedByUser":true},{"id":"n2","label":"SessionEventsHub"}],"edges":[{"id":"e1","from":"n1","to":"n2","label":"publishes","style":"solid"}]}""";
    private const string SequenceState = """{"source":"sequenceDiagram\n  Agent->>Fleet: fleet_canvas_open"}""";

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;
    private string? _csrfToken;

    public async Task InitializeAsync()
    {
        _factory = new ApiWebApplicationFactory(authEnabled: true, useTestAuthentication: true);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        _csrfToken = await GetCsrfTokenAsync(_client);

        using var scope = _factory.Services.CreateScope();
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
                   ('sess-other', 'ws-other', 'inst-other', 'oc-other', 'Other', 'stopped', '/ws-other',
                    'stopped', 'active', '2026-09-01T12:00:00+00:00', 'other-user');
            """);

        await InsertCanvasAsync(connection, "cv_diagram", "sess-mine", "test-user", "diagram", "Session event flow", DiagramState, version: 2, createdAt: "2026-09-12T10:00:00.0000000Z");
        await InsertCanvasAsync(connection, "cv_sequence", "sess-mine", "test-user", "sequence", "Open flow", SequenceState, version: 1, createdAt: "2026-09-12T10:05:00.0000000Z");
        await InsertCanvasAsync(connection, "cv_closed", "sess-mine", "test-user", "diagram", "Old sketch", DiagramState, version: 1, createdAt: "2026-09-12T09:00:00.0000000Z", closedAt: "2026-09-12T09:30:00.0000000Z");
        await InsertCanvasAsync(connection, "cv_mine_2", "sess-mine-2", "test-user", "diagram", "Other session", DiagramState, version: 1, createdAt: "2026-09-12T10:00:00.0000000Z");
        await InsertCanvasAsync(connection, "cv_other", "sess-other", "other-user", "diagram", "Theirs", DiagramState, version: 1, createdAt: "2026-09-12T10:00:00.0000000Z");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task List_returns_the_open_canvases_oldest_first_with_their_full_state()
    {
        var response = await _client!.GetAsync("/api/sessions/sess-mine/canvases");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(
            $$"""[{"canvasId":"cv_diagram","kind":"diagram","title":"Session event flow","version":2,"state":{{DiagramState}}},{"canvasId":"cv_sequence","kind":"sequence","title":"Open flow","version":1,"state":{{SequenceState}}}]""");
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
        var broadcaster = _factory!.Services.GetRequiredService<IEventBroadcaster>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = broadcaster.SubscribeAsync(["session:sess-mine"], "test-user", cts.Token).GetAsyncEnumerator(cts.Token);
        // The subscription registers when the enumerator first runs, so start it before the request.
        var next = events.MoveNextAsync().AsTask();

        var response = await CloseAsync("sess-mine", "cv_diagram");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await next).ShouldBeTrue();
        events.Current.Type.ShouldBe("canvas.closed");
        events.Current.Payload.GetRawText().ShouldBe("""{"sessionId":"sess-mine","canvasId":"cv_diagram"}""");

        var canvases = await ListCanvasIdsAsync("sess-mine");
        canvases.ShouldBe(["cv_sequence"]);
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

        using var scope = _factory!.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var closedAt = await connection.ExecuteScalarAsync<string?>("SELECT closed_at FROM canvases WHERE id = 'cv_other'");
        closedAt.ShouldBeNull();
    }

    private async Task<HttpResponseMessage> CloseAsync(string sessionId, string canvasId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/sessions/{sessionId}/canvases/{canvasId}");
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
