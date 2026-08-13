using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using TestHarnessClass = WeaveFleet.TestHarness.TestHarness;
using TestHarnessRuntimeClass = WeaveFleet.TestHarness.TestHarnessRuntime;

namespace WeaveFleet.IntegrationTests.Sessions;

/// <summary>
/// Integration tests that connect a real SignalR client to the real API hub and verify
/// the event wire format matches what the frontend expects.
///
/// These tests exercise the full pipeline: EventPublisher -> Broadcaster -> Hub -> SignalR Client.
/// No browser, no Playwright, no frontend build required.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SignalREventContractTests : IAsyncLifetime, IDisposable
{
    private SignalRTestServer _server = null!;
    private HubConnection _hub = null!;
    private readonly List<ReceivedEvent> _receivedEvents = [];
    private readonly List<string> _closedEvents = [];
    private readonly List<string> _errorEvents = [];
    private readonly List<string> _rawEvents = [];
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

        _hub.On<string, long, JsonElement>("Event", (topic, eventId, data) =>
        {
            _receivedEvents.Add(new ReceivedEvent(topic, eventId, data));
            _rawEvents.Add(data.GetRawText());
            _eventReceived.Release();
        });

        _hub.Closed += ex =>
        {
            _closedEvents.Add(ex?.Message ?? "no error");
            return Task.CompletedTask;
        };

        await _hub.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Hub_sends_event_with_type_and_properties_shape()
    {
        // Arrange: create a session so we have a valid topic
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        // Subscribe to the session (this also returns a snapshot, but we only care about live events)
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        snapshot.ValueKind.ShouldBe(JsonValueKind.Object);

        // Wait for the hub pump to be subscribed to the broadcaster
        var subscriberCount = await WaitForBroadcasterSubscriberAsync();

        // Act: publish a message.updated event through the broadcaster
        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();
        var payload = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg-test-1",
                role = "assistant",
                sessionID = sessionId,
                time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            parts = new[]
            {
                new { type = "text", id = "part-1", sessionID = sessionId, messageID = "msg-test-1", text = "Hello world" }
            }
        });

        // Broadcast with userId = "local-user" to match the hub pump's subscriber filter
        await broadcaster.BroadcastAsync(
            topic,
            "message.updated",
            payload,
            eventId: 1,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        // Assert: the client receives an event matching WebSocketEvent shape
        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));

        // Build diagnostic info for failure
        var diag = string.Join(Environment.NewLine, new[]
        {
            $"Broadcaster subscriber count: {subscriberCount}",
            $"Session ID: {sessionId}",
            $"Topic: {topic}",
            $"Hub state: {_hub.State}",
            $"Received events count: {_receivedEvents.Count}",
            $"Closed events: {string.Join("; ", _closedEvents)}",
            $"Error events: {string.Join("; ", _errorEvents)}",
            $"Raw events: {string.Join("; ", _rawEvents)}",
        });

        received.ShouldNotBeNull(diag);

        received.Topic.ShouldBe(topic);
        received.EventId.ShouldBe(1);

        // The critical assertion: the data payload must have "type" and "properties" fields
        // This is what the frontend's handleEvent expects
        received.Data.TryGetProperty("type", out var typeProperty).ShouldBeTrue(
            $"Event data missing 'type' field. Actual JSON: {received.Data.GetRawText()}");
        typeProperty.GetString().ShouldBe("message.updated");

        received.Data.TryGetProperty("properties", out var propertiesProperty).ShouldBeTrue(
            $"Event data missing 'properties' field. Actual JSON: {received.Data.GetRawText()}");
        propertiesProperty.ValueKind.ShouldBe(JsonValueKind.Object);

        // Verify the properties contain the payload we sent
        propertiesProperty.TryGetProperty("info", out var info).ShouldBeTrue(
            $"Properties missing 'info'. Actual properties: {propertiesProperty.GetRawText()}");
        info.GetProperty("id").GetString().ShouldBe("msg-test-1");
    }

    [Fact]
    public async Task Hub_sends_message_part_delta_with_correct_shape()
    {
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();
        var payload = JsonSerializer.SerializeToElement(new
        {
            sessionID = sessionId,
            messageID = "msg-test-1",
            partID = "part-1",
            field = "text",
            delta = "Hello "
        });

        await broadcaster.BroadcastAsync(
            topic,
            "message.part.delta",
            payload,
            eventId: 2,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No delta event received");

        received.Data.TryGetProperty("type", out var typeEl).ShouldBeTrue(
            $"Missing 'type'. Actual: {received.Data.GetRawText()}");
        typeEl.GetString().ShouldBe("message.part.delta");

        received.Data.TryGetProperty("properties", out var props).ShouldBeTrue(
            $"Missing 'properties'. Actual: {received.Data.GetRawText()}");
        props.GetProperty("delta").GetString().ShouldBe("Hello ");
        props.GetProperty("messageID").GetString().ShouldBe("msg-test-1");
    }

    [Fact]
    public async Task Hub_sends_activity_status_event_with_correct_shape()
    {
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();
        var payload = JsonSerializer.SerializeToElement(new
        {
            activityStatus = "busy"
        });

        await broadcaster.BroadcastAsync(
            topic,
            "activity_status",
            payload,
            eventId: 3,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No activity_status event received");

        received.Data.TryGetProperty("type", out var typeEl).ShouldBeTrue(
            $"Missing 'type'. Actual: {received.Data.GetRawText()}");
        typeEl.GetString().ShouldBe("activity_status");

        received.Data.TryGetProperty("properties", out var props).ShouldBeTrue(
            $"Missing 'properties'. Actual: {received.Data.GetRawText()}");
        props.GetProperty("activityStatus").GetString().ShouldBe("busy");
    }

    [Fact]
    public async Task Hub_sends_activity_status_retry_event_with_correct_shape()
    {
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();
        var nextRetryTime = DateTimeOffset.UtcNow.AddSeconds(30);
        var payload = JsonSerializer.SerializeToElement(new
        {
            activityStatus = "retry",
            attempt = 2,
            message = "Rate limit exceeded, retrying...",
            next = nextRetryTime.ToString("O")
        });

        await broadcaster.BroadcastAsync(
            topic,
            "activity_status",
            payload,
            eventId: 4,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No activity_status retry event received");

        received.Data.TryGetProperty("type", out var typeEl).ShouldBeTrue(
            $"Missing 'type'. Actual: {received.Data.GetRawText()}");
        typeEl.GetString().ShouldBe("activity_status");

        received.Data.TryGetProperty("properties", out var props).ShouldBeTrue(
            $"Missing 'properties'. Actual: {received.Data.GetRawText()}");
        
        props.GetProperty("activityStatus").GetString().ShouldBe("retry");
        props.GetProperty("attempt").GetInt32().ShouldBe(2);
        props.GetProperty("message").GetString().ShouldBe("Rate limit exceeded, retrying...");
        
        // Verify 'next' is present and is a valid ISO timestamp
        props.TryGetProperty("next", out var nextProp).ShouldBeTrue(
            $"Missing 'next' field in retry event. Actual properties: {props.GetRawText()}");
        var nextStr = nextProp.GetString();
        nextStr.ShouldNotBeNullOrEmpty();
        
        // Verify it's a valid ISO 8601 timestamp
        DateTimeOffset.TryParse(nextStr, out var parsedNext).ShouldBeTrue(
            $"'next' field is not a valid ISO timestamp: {nextStr}");
        
        // Verify it's approximately the time we sent (within 1 second tolerance)
        var diff = Math.Abs((parsedNext - nextRetryTime).TotalSeconds);
        diff.ShouldBeLessThan(1.0, 
            $"'next' timestamp differs too much. Expected: {nextRetryTime:O}, Actual: {parsedNext:O}");
    }

    [Fact]
    public async Task Hub_delivers_rapid_activity_status_events_losslessly_in_order()
    {
        // Arrange: create a session and subscribe
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Wait for the hub pump to be subscribed to the broadcaster
        await WaitForBroadcasterSubscriberAsync();

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();

        // Clear any events received during subscription (snapshot, etc.)
        var initialCount = _receivedEvents.Count;

        // Act: rapidly broadcast busy then idle
        var busyPayload = JsonSerializer.SerializeToElement(new { activityStatus = "busy" });
        var idlePayload = JsonSerializer.SerializeToElement(new { activityStatus = "idle" });

        await broadcaster.BroadcastAsync(
            topic,
            "activity_status",
            busyPayload,
            eventId: 100,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        await broadcaster.BroadcastAsync(
            topic,
            "activity_status",
            idlePayload,
            eventId: 101,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        // Assert: wait for both events to arrive
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_receivedEvents.Count < initialCount + 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var newEvents = _receivedEvents.Skip(initialCount).ToList();
        newEvents.Count.ShouldBe(2, $"Expected 2 events but received {newEvents.Count}. Events: {string.Join(", ", newEvents.Select(e => $"[{e.EventId}:{e.Data.GetProperty("type").GetString()}]"))}");

        var first = newEvents[0];
        first.EventId.ShouldBe(100);
        first.Data.GetProperty("properties").GetProperty("activityStatus").GetString().ShouldBe("busy");

        var second = newEvents[1];
        second.EventId.ShouldBe(101);
        second.Data.GetProperty("properties").GetProperty("activityStatus").GetString().ShouldBe("idle");
    }

    [Fact]
    public async Task Snapshot_returns_messages_on_subscribe()
    {
        var sessionId = await CreateSessionAsync();

        // Subscribe and verify snapshot structure
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        snapshot.ValueKind.ShouldBe(JsonValueKind.Object);

        // Snapshot should have messages array (may be empty for new session)
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue(
            $"Snapshot missing 'messages'. Actual: {snapshot.GetRawText()}");
        messages.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task Snapshot_includes_tool_output_in_completed_state()
    {
        // Arrange: create a session and persist a message with a completed tool part
        var sessionId = await CreateSessionAsync();
        await PersistMessageWithCompletedToolAsync(sessionId);

        // Act: subscribe and get snapshot
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Assert: snapshot contains messages
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue(
            $"Snapshot missing 'messages'. Actual: {snapshot.GetRawText()}");
        messages.ValueKind.ShouldBe(JsonValueKind.Array);
        messages.GetArrayLength().ShouldBeGreaterThan(0, "Expected at least one message in snapshot");

        // Find the message with tool parts
        var messageWithTool = messages.EnumerateArray()
            .FirstOrDefault(m => m.TryGetProperty("parts", out var parts) 
                && parts.EnumerateArray().Any(p => p.TryGetProperty("type", out var t) && t.GetString() == "tool"));

        messageWithTool.ValueKind.ShouldNotBe(JsonValueKind.Undefined, 
            $"No message with tool part found. Messages: {messages.GetRawText()}");

        // Find the tool part
        messageWithTool.TryGetProperty("parts", out var messageParts).ShouldBeTrue();
        var toolPart = messageParts.EnumerateArray()
            .First(p => p.TryGetProperty("type", out var t) && t.GetString() == "tool");

        // Verify tool part has state
        toolPart.TryGetProperty("state", out var state).ShouldBeTrue(
            $"Tool part missing 'state'. Actual: {toolPart.GetRawText()}");

        // THE CRITICAL ASSERTION: state must have 'output' field for completed tools
        // The client's getToolOutput() function looks for state.output
        state.TryGetProperty("output", out var output).ShouldBeTrue(
            $"Tool state missing 'output' field. This is the bug! Actual state JSON: {state.GetRawText()}");

        // Verify output contains the expected data
        output.ValueKind.ShouldNotBe(JsonValueKind.Null);
        output.TryGetProperty("result", out var result).ShouldBeTrue(
            $"Tool output missing 'result'. Actual: {output.GetRawText()}");
        result.GetString().ShouldBe("test output");
    }

    [Fact]
    public async Task Hub_auto_subscribes_to_child_session_events_after_delegation()
    {
        // Arrange: create parent and child sessions
        var parentSessionId = await CreateSessionAsync();
        var childSessionId = await CreateSessionAsync();
        var parentTopic = $"session:{parentSessionId}";
        var childTopic = $"session:{childSessionId}";

        // Subscribe to parent session only
        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", parentSessionId);

        // Wait for the hub pump to be subscribed to the broadcaster
        await WaitForBroadcasterSubscriberAsync();

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();

        // Clear any events received during subscription
        var initialCount = _receivedEvents.Count;

        // Act: broadcast a delegation.updated event to the parent session with childSessionId
        var delegationPayload = JsonSerializer.SerializeToElement(new
        {
            childSessionId = childSessionId,
            status = "active"
        });

        await broadcaster.BroadcastAsync(
            parentTopic,
            "delegation.updated",
            delegationPayload,
            eventId: 200,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        // Wait for the delegation event to be processed
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_receivedEvents.Count < initialCount + 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // Now broadcast a message.updated event to the CHILD session
        var childMessagePayload = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg-child-1",
                role = "assistant",
                sessionID = childSessionId,
                time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            parts = new[]
            {
                new { type = "text", id = "part-child-1", sessionID = childSessionId, messageID = "msg-child-1", text = "Child response" }
            }
        });

        await broadcaster.BroadcastAsync(
            childTopic,
            "message.updated",
            childMessagePayload,
            eventId: 201,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        // Assert: the client should receive the child session event
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (_receivedEvents.Count < initialCount + 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var newEvents = _receivedEvents.Skip(initialCount).ToList();
        newEvents.Count.ShouldBe(2, 
            $"Expected 2 events (delegation + child message) but received {newEvents.Count}. " +
            $"Events: {string.Join(", ", newEvents.Select(e => $"[{e.EventId}:{e.Data.GetProperty("type").GetString()}@{e.Topic}]"))}");

        // Verify delegation event
        var delegationEvent = newEvents[0];
        delegationEvent.Topic.ShouldBe(parentTopic);
        delegationEvent.EventId.ShouldBe(200);
        delegationEvent.Data.GetProperty("type").GetString().ShouldBe("delegation.updated");

        // Verify child message event
        var childEvent = newEvents[1];
        childEvent.Topic.ShouldBe(childTopic, 
            "Child session event should be delivered on child topic after auto-subscription");
        childEvent.EventId.ShouldBe(201);
        childEvent.Data.GetProperty("type").GetString().ShouldBe("message.updated");
        childEvent.Data.GetProperty("properties").GetProperty("info").GetProperty("id").GetString()
            .ShouldBe("msg-child-1");
    }

    [Fact]
    public async Task Hub_cleans_up_child_subscriptions_on_parent_unsubscribe()
    {
        // Arrange: create parent and child sessions
        var parentSessionId = await CreateSessionAsync();
        var childSessionId = await CreateSessionAsync();
        var parentTopic = $"session:{parentSessionId}";
        var childTopic = $"session:{childSessionId}";

        // Subscribe to parent session only
        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", parentSessionId);

        // Wait for the hub pump to be subscribed to the broadcaster
        await WaitForBroadcasterSubscriberAsync();

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();

        // Clear any events received during subscription
        var initialCount = _receivedEvents.Count;

        // Broadcast delegation.updated to trigger auto-subscription to child
        var delegationPayload = JsonSerializer.SerializeToElement(new
        {
            childSessionId = childSessionId,
            status = "active"
        });

        await broadcaster.BroadcastAsync(
            parentTopic,
            "delegation.updated",
            delegationPayload,
            eventId: 300,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        // Wait for delegation event to be processed
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_receivedEvents.Count < initialCount + 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // Sanity check: verify child events arrive before unsubscribe
        var childMessagePayload1 = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg-child-before",
                role = "assistant",
                sessionID = childSessionId,
                time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            parts = new[]
            {
                new { type = "text", id = "part-child-before", sessionID = childSessionId, messageID = "msg-child-before", text = "Before unsubscribe" }
            }
        });

        await broadcaster.BroadcastAsync(
            childTopic,
            "message.updated",
            childMessagePayload1,
            eventId: 301,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (_receivedEvents.Count < initialCount + 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        _receivedEvents.Skip(initialCount).Count().ShouldBe(2, "Should receive delegation + child message before unsubscribe");

        // Act: unsubscribe from parent
        await _hub.InvokeAsync("UnsubscribeFromSessionAsync", parentSessionId);

        // Give the hub time to process the unsubscribe
        await Task.Delay(200);

        // Clear received events to isolate post-unsubscribe behavior
        var countBeforeTest = _receivedEvents.Count;

        // Broadcast another event to child topic
        var childMessagePayload2 = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg-child-after",
                role = "assistant",
                sessionID = childSessionId,
                time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            parts = new[]
            {
                new { type = "text", id = "part-child-after", sessionID = childSessionId, messageID = "msg-child-after", text = "After unsubscribe" }
            }
        });

        await broadcaster.BroadcastAsync(
            childTopic,
            "message.updated",
            childMessagePayload2,
            eventId: 302,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        // Assert: the child event should NOT be received (use a short timeout)
        await Task.Delay(1000);

        var eventsAfterUnsubscribe = _receivedEvents.Skip(countBeforeTest).ToList();
        eventsAfterUnsubscribe.Count.ShouldBe(0, 
            $"Expected no events after unsubscribing from parent, but received {eventsAfterUnsubscribe.Count}. " +
            $"Events: {string.Join(", ", eventsAfterUnsubscribe.Select(e => $"[{e.EventId}:{e.Data.GetProperty("type").GetString()}@{e.Topic}]"))}");
    }

    private async Task PersistMessageWithCompletedToolAsync(string sessionId)
    {
        var messageRepo = _server.Services.GetRequiredService<IMessageRepository>();
        
        var messageId = $"msg-{Guid.NewGuid():N}";
        var toolCallId = $"call-{Guid.NewGuid():N}";
        
        // Create a message with a tool use part and a tool result part
        var parts = new MessagePart[]
        {
            new ToolUsePart(
                ToolCallId: toolCallId,
                ToolName: "bash",
                Arguments: JsonSerializer.SerializeToElement(new { command = "echo test" }),
                State: ToolUseState.Completed),
            new ToolResultPart(
                ToolCallId: toolCallId,
                Content: JsonSerializer.Serialize(new { result = "test output" }),
                IsError: false)
        };

        var harnessMessage = new HarnessMessage
        {
            Id = messageId,
            Role = "assistant",
            Parts = parts,
            Timestamp = DateTimeOffset.UtcNow,
            Agent = null,
            ModelId = null
        };

        var persistedMessage = MessagePersistenceService.ToPersistedMessage(sessionId, harnessMessage);
        await messageRepo.UpsertAsync(persistedMessage);
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
            title = $"SignalR Contract Test {Guid.NewGuid():N}",
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

    private async Task<ReceivedEvent?> WaitForEventAsync(TimeSpan timeout)
    {
        if (await _eventReceived.WaitAsync(timeout))
        {
            return _receivedEvents[^1];
        }

        return null;
    }

    private async Task<int> WaitForBroadcasterSubscriberAsync()
    {
        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();
        if (broadcaster is not InMemoryEventBroadcaster inMemory)
        {
            await Task.Delay(500);
            return -1;
        }

        // Wait up to 5s for at least one subscriber (the hub pump)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (inMemory.SubscriberCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        return inMemory.SubscriberCount;
    }

    private sealed record ReceivedEvent(string Topic, long EventId, JsonElement Data);
}

/// <summary>
/// Lightweight test server that boots the real API with Kestrel (no Playwright, no frontend build).
/// </summary>
internal sealed class SignalRTestServer : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fleet-signalr-test-{Guid.NewGuid():N}.db");
    private readonly string _analyticsDbPath = Path.Combine(Path.GetTempPath(), $"fleet-signalr-analytics-test-{Guid.NewGuid():N}.db");
    private IHost? _host;
    private string? _serverUrl;

    public TestHarnessClass TestHarness { get; } = new();
    public TestHarnessRuntimeClass TestHarnessRuntime { get; } = new();
    public string ServerUrl => _serverUrl ?? throw new InvalidOperationException("Not started");
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Not started");

    public async Task StartAsync()
    {
        var factory = new TestWebApplicationFactory(_dbPath, _analyticsDbPath, TestHarness, TestHarnessRuntime);

        // Trigger host creation
        try { _ = factory.Services; }
        catch (InvalidCastException) { /* expected: base tries to cast Kestrel to TestServer */ }

        _host = factory.Host;
        _serverUrl = factory.ServerUrl;

        // Register workspace root
        using var scope = _host.Services.CreateScope();
        var workspaceRootService = scope.ServiceProvider.GetRequiredService<WorkspaceRootService>();
        var tempRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        await workspaceRootService.AddRootAsync(tempRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        TryDelete(_dbPath);
        TryDelete($"{_dbPath}-wal");
        TryDelete($"{_dbPath}-shm");
        TryDelete(_analyticsDbPath);
        TryDelete($"{_analyticsDbPath}-wal");
        TryDelete($"{_analyticsDbPath}-shm");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath;
        private readonly string _analyticsDbPath;
        private readonly TestHarnessClass _testHarness;
        private readonly TestHarnessRuntimeClass _testHarnessRuntime;
        private IHost? _host;

        public TestWebApplicationFactory(
            string dbPath, string analyticsDbPath,
            TestHarnessClass testHarness, TestHarnessRuntimeClass testHarnessRuntime)
        {
            _dbPath = dbPath;
            _analyticsDbPath = analyticsDbPath;
            _testHarness = testHarness;
            _testHarnessRuntime = testHarnessRuntime;
        }

        public IHost Host => _host ?? throw new InvalidOperationException("Not started");
        public string ServerUrl { get; private set; } = "";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                // Remove production harness registrations
                var toRemove = services
                    .Where(d =>
                        d.ServiceType == typeof(IHarness) ||
                        d.ServiceType == typeof(IHarnessRuntime) ||
                        d.ServiceType == typeof(OpenCodeHarness) ||
                        d.ServiceType == typeof(OpenCodeHarnessRuntime) ||
                        d.ServiceType == typeof(ClaudeCodeHarness) ||
                        d.ServiceType == typeof(ClaudeCodeHarnessRuntime))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);

                services.AddSingleton<IHarness>(_testHarness);
                services.AddSingleton<IHarnessRuntime>(sp =>
                {
                    _testHarnessRuntime.SetScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
                    return _testHarnessRuntime;
                });

                // Remove pool health check
                var poolHealth = services.Where(d => d.ServiceType == typeof(IOpenCodePoolHealthCheck)).ToList();
                foreach (var d in poolHealth) services.Remove(d);
                services.AddSingleton<IOpenCodePoolHealthCheck, EmptyPoolHealth>();

                // Replace FleetOptions and DB
                var existingOptions = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(FleetOptions) && d.Lifetime == ServiceLifetime.Singleton);
                if (existingOptions is not null) services.Remove(existingOptions);

                var connFactory = services.Where(d => d.ServiceType == typeof(IDbConnectionFactory)).ToList();
                foreach (var d in connFactory) services.Remove(d);

                var portAlloc = services.Where(d => d.ServiceType.Name == "PortAllocator").ToList();
                foreach (var d in portAlloc) services.Remove(d);

                var testOptions = new FleetOptions
                {
                    DatabasePath = _dbPath,
                    AnalyticsDatabasePath = _analyticsDbPath,
                    AnalyticsEnabled = false,
                    Port = 0,
                    Host = "127.0.0.1",
                    Auth = new AuthOptions { Enabled = false, TokenAuthEnabled = false },
                };

                services.AddSingleton(testOptions);
                services.AddSingleton(new PortAllocator(
                    testOptions.HarnessPortRangeStart, testOptions.HarnessPortRangeEnd));
                services.AddSingleton<IDbConnectionFactory>(
                    _ => new WeaveFleet.Infrastructure.Data.SqliteConnectionFactory(testOptions));
            });

            builder.UseUrls("http://127.0.0.1:0");
            builder.UseSetting("Urls", "http://127.0.0.1:0");
            builder.UseSetting("Fleet:DatabasePath", _dbPath);
            builder.UseSetting("Fleet:AnalyticsDatabasePath", _analyticsDbPath);
            builder.UseSetting("Fleet:AnalyticsEnabled", "false");
            builder.UseSetting("Fleet:Port", "0");
            builder.UseSetting("Fleet:Host", "127.0.0.1");
            builder.UseSetting("Fleet:Auth:Enabled", "false");
            builder.UseSetting("Fleet:Auth:TokenAuthEnabled", "false");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureWebHost(wb => wb.UseKestrel());
            _host = builder.Build();
            _host.Start();

            var server = _host.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()!;
            ServerUrl = addresses.Addresses.First();

            return _host;
        }

        private sealed class EmptyPoolHealth : IOpenCodePoolHealthCheck
        {
            public OpenCodePoolHealthStatus GetStatus() => new(0, 0, WarmCount: 0, ActiveCount: 0, []);
        }
    }
}
