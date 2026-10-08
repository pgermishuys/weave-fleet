using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.TestHarness;

namespace WeaveFleet.IntegrationTests.Sessions;

/// <summary>
/// Opening a session while its reply streams, or coming back to it, or reloading, subscribes afresh. The snapshot has to
/// carry the reply so far: a harness may not have kept it yet (Claude Code saves a message once it's done, OpenCode a
/// part's text once the part ends), and the deltas that streamed before the subscribe don't come again.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SignalRMidReplySnapshotTests : IAsyncLifetime, IDisposable
{
    private SignalRTestServer _server = null!;
    private HubConnection _watcher = null!;
    private readonly ConcurrentQueue<JsonElement> _deltas = new();

    public async Task InitializeAsync()
    {
        _server = new SignalRTestServer();
        await _server.StartAsync();

        _watcher = new HubConnectionBuilder().WithUrl($"{_server.ServerUrl}/hubs/session-events").Build();
        _watcher.On<string, long?, JsonElement>("Event", (_, _, data) =>
        {
            if (data.GetProperty("type").GetString() == "message.part.delta.streamed")
                _deltas.Enqueue(data.GetProperty("properties").Clone());
        });
        await _watcher.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _watcher.DisposeAsync();
        await _server.DisposeAsync();
    }

    public void Dispose()
    {
    }

    [Fact]
    public async Task A_snapshot_taken_mid_reply_has_the_text_streamed_so_far()
    {
        var (sessionId, harness) = await StartSessionAsync();
        await _watcher.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // The harness streams a reply it hasn't kept yet: no message.updated, so its history doesn't have it.
        await harness.PushEventAsync(Event(harness, sessionId, "session.status", new { sessionID = harness.InstanceId, status = new { type = "busy" } }));
        foreach (var delta in new[] { "Checking the ", "rate limit ", "headers" })
        {
            await harness.PushEventAsync(Event(harness, sessionId, "message.part.delta", new
            {
                sessionID = harness.InstanceId,
                messageID = "msg_reply",
                partID = "prt_reply",
                field = "text",
                delta,
            }));
        }
        await EventuallyAsync(() => _deltas.Count == 3);

        // Someone opens the session now (another tab, a reload, coming back to it).
        await using var opener = new HubConnectionBuilder().WithUrl($"{_server.ServerUrl}/hubs/session-events").Build();
        await opener.StartAsync();
        var snapshot = await opener.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        var reply = snapshot.GetProperty("messages").EnumerateArray()
            .SingleOrDefault(message => message.GetProperty("info").GetProperty("id").GetString() == "msg_reply");
        reply.ValueKind.ShouldBe(JsonValueKind.Object, $"The snapshot has no reply. Actual: {snapshot.GetProperty("messages").GetRawText()}");
        reply.GetProperty("info").GetProperty("role").GetString().ShouldBe("assistant");
        var part = reply.GetProperty("parts").EnumerateArray().ShouldHaveSingleItem();
        part.GetProperty("id").GetString().ShouldBe("prt_reply");
        part.GetProperty("text").GetString().ShouldBe("Checking the rate limit headers");

        // Each delta says where it starts, so a client that joined with the text so far skips what it has.
        _deltas.Select(delta => delta.GetProperty("offset").GetInt32()).ShouldBe([0, 13, 24]);
    }

    [Fact]
    public async Task The_streamed_text_is_dropped_when_the_turn_ends()
    {
        var (sessionId, harness) = await StartSessionAsync();
        await _watcher.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        await harness.PushEventAsync(Event(harness, sessionId, "session.status", new { sessionID = harness.InstanceId, status = new { type = "busy" } }));
        await harness.PushEventAsync(Event(harness, sessionId, "message.part.delta", new
        {
            sessionID = harness.InstanceId,
            messageID = "msg_reply",
            partID = "prt_reply",
            field = "text",
            delta = "Half a reply",
        }));
        await EventuallyAsync(() => _deltas.Count == 1);
        await harness.PushEventAsync(Event(harness, sessionId, "session.idle", new { sessionID = harness.InstanceId }));

        // By the end of a turn the harness keeps its own reply; Fleet's copy of the deltas would only go stale.
        await EventuallyAsync(async () =>
        {
            var snapshot = await _watcher.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
            return snapshot.GetProperty("activityStatus").GetString() == "idle"
                && snapshot.GetProperty("messages").EnumerateArray().All(message => message.GetProperty("info").GetProperty("id").GetString() != "msg_reply");
        });
    }

    private async Task<(string SessionId, TestHarnessSession Harness)> StartSessionAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server.ServerUrl) };
        var directory = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        await http.PostAsync("/api/workspace-roots",
            new StringContent(JsonSerializer.Serialize(new { path = directory }), System.Text.Encoding.UTF8, "application/json"));
        var response = await http.PostAsync("/api/sessions", new StringContent(
            JsonSerializer.Serialize(new { directory, title = "Rate limit headers", harnessType = "opencode" }),
            System.Text.Encoding.UTF8,
            "application/json"));
        response.EnsureSuccessStatusCode();

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var harness = _server.Services.GetRequiredService<InstanceTracker>()
            .Get(body.GetProperty("instanceId").GetString()!)
            .ShouldBeOfType<TestHarnessSession>();
        return (body.GetProperty("session").GetProperty("id").GetString()!, harness);
    }

    private static HarnessEvent Event(TestHarnessSession harness, string sessionId, string type, object payload) => new()
    {
        Type = type,
        SessionId = harness.InstanceId,
        FleetSessionId = sessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(payload),
    };

    private static Task EventuallyAsync(Func<bool> condition) => EventuallyAsync(() => Task.FromResult(condition()));

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!await condition() && DateTime.UtcNow < until)
            await Task.Delay(100);
        (await condition()).ShouldBeTrue();
    }
}
