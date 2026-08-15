using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace WeaveFleet.IntegrationTests.Sessions;

/// <summary>
/// Integration tests that verify snapshot message ordering uses timestamp (not created_at)
/// and that Time.Created in the payload matches timestamp.
///
/// These tests prevent regression after switching from created_at to timestamp ordering.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SignalRSnapshotMessageOrderingTests : IAsyncLifetime, IDisposable
{
    private SignalRTestServer _server = null!;
    private HubConnection _hub = null!;
    private readonly SemaphoreSlim _eventReceived = new(0);

    public void Dispose()
    {
        _eventReceived.Dispose();
    }

    public async Task InitializeAsync()
    {
        _server = new SignalRTestServer();
        await _server.StartAsync();

        _hub = new HubConnectionBuilder()
            .WithUrl($"{_server.ServerUrl}/hubs/session-events")
            .Build();

        await _hub.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Snapshot_orders_messages_by_timestamp_not_created_at()
    {
        // Arrange: configure test harness with messages that have deliberately out-of-order timestamps
        // Message 1: timestamp = T1 (oldest)
        // Message 2: timestamp = T2 (middle)
        // Message 3: timestamp = T3 (newest)
        //
        // Expected order in snapshot (oldest first): msg-1, msg-2, msg-3

        var now = DateTimeOffset.UtcNow;
        var t1 = now.AddSeconds(-30);
        var t2 = now.AddSeconds(-20);
        var t3 = now.AddSeconds(-10);

        _server.TestHarnessRuntime.Configure(scenario =>
        {
            scenario
                .WithAssistantMessage("msg-1", "Message 1 content", t1)
                .WithAssistantMessage("msg-2", "Message 2 content", t2)
                .WithAssistantMessage("msg-3", "Message 3 content", t3);
        });

        // Create a session
        var sessionId = await CreateSessionAsync();

        // Act: subscribe to get the snapshot
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Assert: snapshot contains messages in timestamp order (oldest first)
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue(
            $"Snapshot missing 'messages'. Actual: {snapshot.GetRawText()}");

        var messageArray = messages.EnumerateArray().ToList();
        messageArray.Count.ShouldBe(3, $"Expected 3 messages but got {messageArray.Count}. Messages: {messages.GetRawText()}");

        // Verify order: msg-1 (T1), msg-2 (T2), msg-3 (T3)
        var firstMsg = messageArray[0];
        firstMsg.GetProperty("info").GetProperty("id").GetString().ShouldBe("msg-1",
            "First message should be msg-1 (oldest timestamp T1)");

        var secondMsg = messageArray[1];
        secondMsg.GetProperty("info").GetProperty("id").GetString().ShouldBe("msg-2",
            "Second message should be msg-2 (middle timestamp T2)");

        var thirdMsg = messageArray[2];
        thirdMsg.GetProperty("info").GetProperty("id").GetString().ShouldBe("msg-3",
            "Third message should be msg-3 (newest timestamp T3)");
    }

    [Fact]
    public async Task Snapshot_uses_timestamp_for_time_created_not_created_at()
    {
        // Arrange: configure test harness with a message at a specific timestamp
        var timestamp = DateTimeOffset.UtcNow.AddSeconds(-30);

        _server.TestHarnessRuntime.Configure(scenario =>
        {
            scenario.WithAssistantMessage("msg-test", "Test message content", timestamp);
        });

        // Create a session
        var sessionId = await CreateSessionAsync();

        // Act: subscribe to get the snapshot
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Assert: Time.Created should match timestamp
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue(
            $"Snapshot missing 'messages'. Actual: {snapshot.GetRawText()}");

        var messageArray = messages.EnumerateArray().ToList();
        messageArray.Count.ShouldBe(1, $"Expected 1 message but got {messageArray.Count}");

        var message = messageArray[0];
        var timeCreated = message.GetProperty("info").GetProperty("time").GetProperty("created").GetInt64();

        var expectedTimestamp = timestamp.ToUnixTimeMilliseconds();

        timeCreated.ShouldBe(expectedTimestamp,
            $"Time.Created should match timestamp ({expectedTimestamp}). Actual: {timeCreated}");
    }

    [Fact]
    public async Task Snapshot_orders_messages_with_same_timestamp_by_id_descending()
    {
        // Arrange: configure test harness with messages that have identical timestamps but different IDs
        var sameTimestamp = DateTimeOffset.UtcNow.AddSeconds(-30);

        // All messages have the same timestamp, so ordering should fall back to id DESC in DB,
        // then reversed for display: msg-a, msg-b, msg-c (alphabetical ascending)
        _server.TestHarnessRuntime.Configure(scenario =>
        {
            scenario
                .WithAssistantMessage("msg-a", "Message A content", sameTimestamp)
                .WithAssistantMessage("msg-b", "Message B content", sameTimestamp)
                .WithAssistantMessage("msg-c", "Message C content", sameTimestamp);
        });

        // Create a session
        var sessionId = await CreateSessionAsync();

        // Act: subscribe to get the snapshot
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Assert: messages should be ordered by id DESC in DB, then reversed for display
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue(
            $"Snapshot missing 'messages'. Actual: {snapshot.GetRawText()}");

        var messageArray = messages.EnumerateArray().ToList();
        messageArray.Count.ShouldBe(3, $"Expected 3 messages but got {messageArray.Count}");

        // After reversal: msg-a, msg-b, msg-c (ascending)
        messageArray[0].GetProperty("info").GetProperty("id").GetString().ShouldBe("msg-a");
        messageArray[1].GetProperty("info").GetProperty("id").GetString().ShouldBe("msg-b");
        messageArray[2].GetProperty("info").GetProperty("id").GetString().ShouldBe("msg-c");
    }

    private async Task<string> CreateSessionAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server.ServerUrl) };
        var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        // Register workspace root
        var rootPayload = JsonSerializer.Serialize(new { path = tempDir });
        await http.PostAsync("/api/workspace-roots",
            new StringContent(rootPayload, System.Text.Encoding.UTF8, "application/json"));

        // Create session
        var createPayload = JsonSerializer.Serialize(new
        {
            directory = tempDir,
            title = $"SignalR Message Ordering Test {Guid.NewGuid():N}",
            harnessType = "opencode"
        });
        var response = await http.PostAsync("/api/sessions",
            new StringContent(createPayload, System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        // Try common response shapes
        if (doc.RootElement.TryGetProperty("session", out var sessionObj)
            && sessionObj.TryGetProperty("id", out var idProp))
        {
            return idProp.GetString()!;
        }

        if (doc.RootElement.TryGetProperty("id", out var directId))
        {
            return directId.GetString()!;
        }

        throw new InvalidOperationException($"Could not extract session ID from response: {body}");
    }
}
