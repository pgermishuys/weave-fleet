using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Messages between sessions, as a local Fleet sees them: an agent process calls under <c>/agent/{token}</c>, and
/// <c>fleet_message</c> arrives on the bridge. Only the refusals are here; delivery needs a running harness and is
/// covered by the live tests.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class SessionMessageEndpointTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string Token = "token-1";
    private const string HarnessSessionId = "oc-1";
    private const string Sender = "sess-sender";
    private const string Receiver = "sess-receiver";
    private const string Owner = "local-user";

    private ApiWebApplicationFactory? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _factory = new ApiWebApplicationFactory(
            authEnabled: false,
            tokenAuthEnabled: true,
            simulateLocalhostRequest: true,
            configureTestServices: services =>
            {
                services.AddSingleton<IHarnessCanvasCallerResolver>(new FakeCallers());
                services.AddSingleton<IHarnessBridgeTokens>(new FakeTokens());
                services.AddScoped<ISessionUpdateSender, DeliveredUpdates>();
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
    public async Task An_agent_reaches_the_same_endpoints_under_its_prefix()
    {
        var response = await _client!.GetAsync($"/agent/{Token}/api/sessions/{Receiver}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("token-2")]
    [InlineData("")]
    public async Task A_prefix_with_a_token_Fleet_did_not_hand_out_is_not_found(string token)
    {
        var response = await _client!.GetAsync($"/agent/{token}/api/sessions/{Receiver}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task With_messages_on_an_agent_prompt_is_refused_and_names_the_tool()
    {
        await TurnOnAsync();

        var response = await _client!.PostAsJsonAsync($"/agent/{Token}/api/sessions/{Receiver}/prompt", new { text = "Update the docs." });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorAsync(response)).ShouldBe(SessionMessages.UseTheToolMessage);
    }

    [Fact]
    public async Task With_messages_on_an_agent_cannot_start_a_session_with_a_task()
    {
        await TurnOnAsync();

        var response = await _client!.PostAsJsonAsync($"/agent/{Token}/api/sessions", new { directory = "/ws", initialPrompt = "Update the docs." });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorAsync(response)).ShouldBe("Start the session without an initialPrompt, then use the fleet_message tool to give it the task.");
    }

    [Fact]
    public async Task With_messages_on_a_prompt_that_is_not_from_an_agent_is_not_refused()
    {
        await TurnOnAsync();

        // No such session, so it stops there: the point is that it got past the agent check.
        var response = await _client!.PostAsJsonAsync("/api/sessions/sess-missing/prompt", new { text = "Hello." });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task With_messages_off_the_tool_is_refused_even_from_a_process_that_has_it()
    {
        var response = await MessageAsync(Receiver, "Update the docs.");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorAsync(response)).ShouldBe(SessionMessageBridge.TurnedOffMessage);
    }

    [Fact]
    public async Task The_tool_refuses_this_session_an_unknown_session_and_an_unknown_caller()
    {
        await TurnOnAsync();

        var self = await MessageAsync(Sender, "Hi.");
        var missing = await MessageAsync("sess-missing", "Hi.");
        var stranger = await MessageAsync(Receiver, "Hi.", token: "token-2");

        self.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorAsync(self)).ShouldBe("That's this session. Give the id of another one.");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(missing)).ShouldBe("No session sess-missing. Find session ids with GET $FLEET_URL/api/sessions.");
        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorAsync(stranger)).ShouldBe(CanvasBridge.UnknownCallerMessage);
    }

    [Fact]
    public async Task A_turn_an_update_started_cannot_ask_to_be_told_again()
    {
        await TurnOnAsync();

        // The sender asked the receiver earlier; the receiver's turn ended, and the update started the sender's turn.
        var updates = _factory!.Services.GetRequiredService<SessionUpdates>();
        updates.Watch(new SessionUpdateWatch(Sender, Receiver, Owner, "msg-1"));
        updates.Observe(Receiver, new MessageUpdated
        {
            Payload = new MessageLifecyclePayload
            {
                Info = new MessageEventInfo { Id = "msg-2", Role = "assistant", SessionId = Receiver, ParentId = "msg-1", Time = new MessageEventTime { Created = 0 } },
            },
        });
        updates.Observe(Receiver, new SessionIdled { Payload = new SessionIdledPayload { SessionId = Receiver } });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!updates.IsStartedByUpdate(Sender) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        var response = await MessageAsync(Receiver, "Thanks. Now the changelog.", notifyWhenDone: true);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorAsync(response)).ShouldBe(SessionMessageBridge.NoNotifyFromUpdateMessage);
    }

    private async Task TurnOnAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(Owner))
            await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(SessionMessages.PreferenceKey, "true");
    }

    private async Task<HttpResponseMessage> MessageAsync(string sessionId, string text, string token = Token, bool notifyWhenDone = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bridge/session/message")
        {
            Content = JsonContent.Create(new { harnessSessionId = HarnessSessionId, sessionId, text, notifyWhenDone }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();

    private async Task SeedSessionsAsync()
    {
        using var scope = _factory!.Services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await connection.ExecuteAsync($"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-1', '/ws', 'Work', '2026-09-18T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-1', 0, NULL, '/ws', '', 'running', '2026-09-18T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES ('{Sender}', 'ws-1', 'inst-1', 'oc-1', 'Fix authentication flow', 'active', '/ws',
                    'running', 'active', '2026-09-18T10:00:00+00:00', '{Owner}'),
                   ('{Receiver}', 'ws-1', 'inst-1', 'oc-2', 'Update documentation', 'active', '/ws',
                    'running', 'active', '2026-09-18T10:00:00+00:00', '{Owner}');
            """);
    }

    /// <summary>Delivers every update without prompting anything: the point is what Fleet remembers afterwards.</summary>
    private sealed class DeliveredUpdates : ISessionUpdateSender
    {
        public Task<SessionUpdate?> ReadAsync(SessionUpdateWatch watch, TurnError? failure, CancellationToken ct)
            => Task.FromResult<SessionUpdate?>(new SessionUpdate(watch.AskerId, watch.TargetId, watch.UserId, "Done.", SessionUpdates.Finished));

        public Task<bool> SendAsync(SessionUpdate update, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeTokens : IHarnessBridgeTokens
    {
        public bool IsKnown(string bridgeToken) => bridgeToken == Token;
    }

    private sealed class FakeCallers : IHarnessCanvasCallerResolver
    {
        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(bridgeToken == Token && harnessSessionId == HarnessSessionId
                ? new HarnessCanvasCaller(Sender, Owner)
                : null);
    }
}
