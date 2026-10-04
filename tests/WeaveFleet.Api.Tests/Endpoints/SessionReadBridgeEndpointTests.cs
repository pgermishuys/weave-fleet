using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// <c>fleet_session_read</c> as the harness process calls it, and the references a prompt carries: only the caller's
/// owner's sessions can be read or referenced, and the conversation comes a page at a time.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class SessionReadBridgeEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string Token = "token-1";
    private const string HarnessSessionId = "oc-1";
    private const string Reader = "sess-reader";
    private const string Referenced = "sess-referenced";
    private const string SomeoneElses = "sess-someone-elses";
    private const string Owner = "local-user";

    private readonly FakeSessionMessageProxy _messages = new();
    private readonly List<(string SessionId, int? Limit, string? Before)> _reads = [];
    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        // 45 messages, m01 oldest; a page is the newest `limit` before the cursor, oldest first.
        var all = Enumerable.Range(1, 45).Select(i => Message($"m{i:00}", i % 2 == 1 ? "user" : "assistant", $"Message {i}")).ToList();
        _messages.GetMessagesBehavior = (sessionId, limit, before, _) =>
        {
            _reads.Add((sessionId, limit, before));
            var end = before is null ? all.Count : all.FindIndex(m => m.Id == before);
            var start = Math.Max(0, end - (limit ?? 20));
            return Task.FromResult(new MessagePage(all.GetRange(start, end - start), HasMore: start > 0));
        };

        _factory = new ApiWebApplicationFactory(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            configureTestServices: services =>
            {
                services.AddSingleton<IHarnessCanvasCallerResolver>(new FakeCallers());
                services.RemoveAll<ISessionMessageProxy>();
                services.AddScoped<ISessionMessageProxy>(_ => _messages);
            });
        _client = _factory.CreateClient();
        await SeedSessionsAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Reads_the_latest_page_of_a_session_and_says_how_to_get_older_ones()
    {
        var response = await ReadAsync(Referenced, before: null, limit: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("title").GetString().ShouldBe("Read t3code: what can we learn?");
        var output = body.GetProperty("output").GetString()!;
        output.ShouldStartWith($"Session \"t3code: what can we learn?\" ({Referenced}) · ");
        output.ShouldContain("Its latest 20 messages, oldest first. For older ones, call again with before \"m26\".");
        output.ShouldContain("[user 1970-01-01 00:00 m45]\nMessage 45");
        output.ShouldNotContain("Message 25\n");
        _reads.ShouldBe([(Referenced, 20, null)]);
    }

    [Fact]
    public async Task Pages_back_to_the_start_of_the_session()
    {
        var response = await ReadAsync(Referenced, before: "m06", limit: 10);

        var output = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("output").GetString()!;
        output.ShouldContain("The 5 messages before that, oldest first. That's the start of the session.");
        output.ShouldContain("Message 1\n");
        output.ShouldContain("Message 5");
        output.ShouldNotContain("Message 6");
    }

    [Fact]
    public async Task Another_users_session_reads_as_not_found()
    {
        var response = await ReadAsync(SomeoneElses, before: null, limit: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(response)).ShouldBe($"No session {SomeoneElses}. Use the id of a session in a <fleet-session-references> block.");
        _reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_caller_reads_nothing()
    {
        var response = await ReadAsync(Referenced, before: null, limit: null, token: "token-2");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(response)).ShouldBe(CanvasBridge.UnknownCallerMessage);
        _reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_size_out_of_range_is_refused()
    {
        var response = await ReadAsync(Referenced, before: null, limit: 500);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorAsync(response)).ShouldBe("\"limit\" is from 1 to 50.");
    }

    [Fact]
    public async Task A_prompt_referencing_another_users_session_is_refused_before_it_is_sent()
    {
        var response = await _client!.PostAsJsonAsync($"/api/sessions/{Reader}/prompt", new
        {
            text = "Use @their-notes",
            sessionReferences = new[] { new { token = "@their-notes", sessionId = SomeoneElses } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(response)).ShouldBe("@their-notes names a session Fleet can't find. Pick it again from the @ list.");
    }

    [Fact]
    public async Task A_queued_message_referencing_another_users_session_is_refused()
    {
        var response = await _client!.PostAsJsonAsync($"/api/sessions/{Reader}/queue", new
        {
            text = "Use @their-notes",
            sessionReferences = new[] { new { token = "@their-notes", sessionId = SomeoneElses } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorAsync(response)).ShouldBe("@their-notes names a session Fleet can't find. Pick it again from the @ list.");
    }

    private async Task<HttpResponseMessage> ReadAsync(string sessionId, string? before, int? limit, string token = Token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bridge/session/read")
        {
            Content = JsonContent.Create(new { harnessSessionId = HarnessSessionId, sessionId, before, limit }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private static HarnessMessage Message(string id, string role, string text) => new()
    {
        Id = id,
        Role = role,
        Parts = [new TextPart(text)],
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    private async Task SeedSessionsAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync($"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-1', '/ws', 'Work', '2026-10-04T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-1', 0, NULL, '/ws', '', 'running', '2026-10-04T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES ('{Reader}', 'ws-1', 'inst-1', 'oc-1', 'Capture subagents', 'active', '/ws',
                    'running', 'active', '2026-10-04T10:00:00+00:00', '{Owner}'),
                   ('{Referenced}', 'ws-1', 'inst-1', 'oc-2', 't3code: what can we learn?', 'active', '/ws',
                    'running', 'active', '2026-10-04T10:00:00+00:00', '{Owner}'),
                   ('{SomeoneElses}', 'ws-1', 'inst-1', 'oc-3', 'Their notes', 'active', '/ws',
                    'running', 'active', '2026-10-04T10:00:00+00:00', 'someone-else');
            """);
    }

    private sealed class FakeCallers : IHarnessCanvasCallerResolver
    {
        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(bridgeToken == Token && harnessSessionId == HarnessSessionId
                ? new HarnessCanvasCaller(Reader, Owner)
                : null);
    }
}
