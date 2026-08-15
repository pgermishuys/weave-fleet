using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Sessions;

/// <summary>
/// Integration tests that reproduce the bug where navigating away from a session with active
/// delegations and back loses the busy/delegation state.
/// 
/// Bug: When a session has an active sub-agent delegation (task tool), the user navigates to
/// another session and back. On return, the snapshot should show delegations with "running"
/// status and activity as "busy"/"delegating", but it doesn't.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SignalRDelegationResubscribeTests : IAsyncLifetime, IDisposable
{
    private SignalRTestServer _server = null!;
    private HubConnection _hub = null!;
    private readonly List<ReceivedEvent> _receivedEvents = [];
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
            _eventReceived.Release();
        });

        await _hub.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _server.DisposeAsync();
    }

    /// <summary>
    /// Test 1: Verify that after navigating away from a session with an active delegation
    /// and back, the snapshot includes the delegation with "running" status and "busy" activity.
    /// </summary>
    [Fact]
    public async Task Resubscribe_after_navigation_snapshot_includes_active_delegations()
    {
        // Arrange: create session A and session B
        var sessionA = await CreateSessionAsync();
        var sessionB = await CreateSessionAsync();

        // Subscribe to session A
        var snapshot1 = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionA);
        snapshot1.ValueKind.ShouldBe(JsonValueKind.Object);

        // Insert a delegation into DB with status "running"
        var delegationRepo = _server.Services.GetRequiredService<IDelegationRepository>();
        var delegation = new Delegation
        {
            Id = $"delegation-{Guid.NewGuid():N}",
            ParentSessionId = sessionA,
            ChildSessionId = null,
            ParentToolCallId = $"call-{Guid.NewGuid():N}",
            Title = "Test delegation",
            Status = "running",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            CompletedAt = null
        };
        await delegationRepo.InsertAsync(delegation);

        // Set activity status to "busy"
        var activityTracker = _server.Services.GetRequiredService<SessionActivityTracker>();
        activityTracker.Update(sessionA, "busy", "local-user");

        // Act: navigate away and back
        // Unsubscribe from session A
        await _hub.InvokeAsync("UnsubscribeFromSessionAsync", sessionA);

        // Subscribe to session B (navigate away)
        var snapshot2 = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionB);
        snapshot2.ValueKind.ShouldBe(JsonValueKind.Object);

        // Unsubscribe from session B
        await _hub.InvokeAsync("UnsubscribeFromSessionAsync", sessionB);

        // Re-subscribe to session A
        var snapshot3 = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionA);

        // Assert: snapshot contains delegation with status "running"
        snapshot3.TryGetProperty("delegations", out var delegations).ShouldBeTrue(
            $"Snapshot missing 'delegations'. Actual: {snapshot3.GetRawText()}");
        delegations.ValueKind.ShouldBe(JsonValueKind.Array);
        delegations.GetArrayLength().ShouldBeGreaterThan(0,
            "Snapshot should contain at least one delegation after re-subscribe");

        var firstDelegation = delegations.EnumerateArray().First();
        firstDelegation.GetProperty("status").GetString().ShouldBe("running",
            "Delegation status should be 'running' after re-subscribe");
        firstDelegation.GetProperty("delegationId").GetString().ShouldBe(delegation.Id);

        // Assert: snapshot has activityStatus "busy"
        snapshot3.TryGetProperty("activityStatus", out var activityStatus).ShouldBeTrue(
            $"Snapshot missing 'activityStatus'. Actual: {snapshot3.GetRawText()}");
        activityStatus.GetString().ShouldBe("busy",
            "Activity status should be 'busy' after re-subscribe");
    }

    /// <summary>
    /// Test 2: Verify that when activity is explicitly "idle" but there's a running delegation,
    /// the snapshot includes the delegation. The client-side reducer should derive "delegating"
    /// from the presence of active delegations.
    /// </summary>
    [Fact]
    public async Task Resubscribe_after_navigation_snapshot_shows_delegating_status_when_explicitly_idle_with_running_delegation()
    {
        // Arrange: create session A and session B
        var sessionA = await CreateSessionAsync();
        var sessionB = await CreateSessionAsync();

        // Subscribe to session A
        var snapshot1 = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionA);
        snapshot1.ValueKind.ShouldBe(JsonValueKind.Object);

        // Insert a delegation into DB with status "running"
        var delegationRepo = _server.Services.GetRequiredService<IDelegationRepository>();
        var delegation = new Delegation
        {
            Id = $"delegation-{Guid.NewGuid():N}",
            ParentSessionId = sessionA,
            ChildSessionId = null,
            ParentToolCallId = $"call-{Guid.NewGuid():N}",
            Title = "Test delegation",
            Status = "running",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            CompletedAt = null
        };
        await delegationRepo.InsertAsync(delegation);

        // Set activity status to "idle" (explicitly idle, but delegation is running)
        var activityTracker = _server.Services.GetRequiredService<SessionActivityTracker>();
        activityTracker.Update(sessionA, "idle", "local-user");

        // Act: navigate away and back
        // Unsubscribe from session A
        await _hub.InvokeAsync("UnsubscribeFromSessionAsync", sessionA);

        // Subscribe to session B (navigate away)
        var snapshot2 = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionB);
        snapshot2.ValueKind.ShouldBe(JsonValueKind.Object);

        // Unsubscribe from session B
        await _hub.InvokeAsync("UnsubscribeFromSessionAsync", sessionB);

        // Re-subscribe to session A
        var snapshot3 = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionA);

        // Assert: snapshot contains delegation with status "running"
        snapshot3.TryGetProperty("delegations", out var delegations).ShouldBeTrue(
            $"Snapshot missing 'delegations'. Actual: {snapshot3.GetRawText()}");
        delegations.ValueKind.ShouldBe(JsonValueKind.Array);
        delegations.GetArrayLength().ShouldBeGreaterThan(0,
            "Snapshot should contain at least one delegation after re-subscribe");

        var firstDelegation = delegations.EnumerateArray().First();
        firstDelegation.GetProperty("status").GetString().ShouldBe("running",
            "Delegation status should be 'running' after re-subscribe");

        // Assert: snapshot has activityStatus "idle" (server-side)
        // The client-side reducer should derive "delegating" from the presence of running delegations
        snapshot3.TryGetProperty("activityStatus", out var activityStatus).ShouldBeTrue(
            $"Snapshot missing 'activityStatus'. Actual: {snapshot3.GetRawText()}");
        activityStatus.GetString().ShouldBe("idle",
            "Activity status should be 'idle' (server-side), client will derive 'delegating' from delegations");
    }

    /// <summary>
    /// Test 3: Verify that child session events are received after re-subscribing to a parent
    /// session with an active delegation.
    /// 
    /// THIS TEST SHOULD FAIL (RED phase) because UnsubscribeFromSessionAsync removes child topic
    /// subscriptions, but SubscribeToSessionAsync does NOT restore them from existing delegations.
    /// </summary>
    [Fact]
    public async Task Child_session_events_received_after_resubscribe_to_parent_with_active_delegation()
    {
        // Arrange: create parent session A, child session C, and session B
        var parentSessionA = await CreateSessionAsync();
        var childSessionC = await CreateSessionAsync();
        var sessionB = await CreateSessionAsync();

        var parentTopic = $"session:{parentSessionA}";
        var childTopic = $"session:{childSessionC}";

        // Subscribe to parent session A
        var snapshot1 = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", parentSessionA);
        snapshot1.ValueKind.ShouldBe(JsonValueKind.Object);

        // Wait for the hub pump to be subscribed to the broadcaster
        await WaitForBroadcasterSubscriberAsync();

        // Insert a delegation with childSessionId = C and status "running"
        var delegationRepo = _server.Services.GetRequiredService<IDelegationRepository>();
        var delegation = new Delegation
        {
            Id = $"delegation-{Guid.NewGuid():N}",
            ParentSessionId = parentSessionA,
            ChildSessionId = childSessionC,
            ParentToolCallId = $"call-{Guid.NewGuid():N}",
            Title = "Test delegation to child",
            Status = "running",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            CompletedAt = null
        };
        await delegationRepo.InsertAsync(delegation);

        // Simulate delegation.updated event to trigger auto-subscribe to child topic
        // This is what happens when a delegation is created during normal operation
        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();
        var delegationPayload = JsonSerializer.SerializeToElement(new
        {
            childSessionId = childSessionC,
            status = "running"
        });

        await broadcaster.BroadcastAsync(
            parentTopic,
            "delegation.updated",
            delegationPayload,
            eventId: 100,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        // Wait for delegation event to be processed
        var initialCount = _receivedEvents.Count;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_receivedEvents.Count < initialCount + 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // Clear received events
        _receivedEvents.Clear();

        // Verify child events are received BEFORE navigation
        var childMessagePayload1 = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg-child-before",
                role = "assistant",
                sessionID = childSessionC,
                time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            parts = new[]
            {
                new { type = "text", id = "part-child-before", sessionID = childSessionC, messageID = "msg-child-before", text = "Before navigation" }
            }
        });

        await broadcaster.BroadcastAsync(
            childTopic,
            "message.updated",
            childMessagePayload1,
            eventId: 101,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        // Wait for child event to arrive
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (_receivedEvents.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        _receivedEvents.Count.ShouldBeGreaterThan(0, "Child event should be received before navigation");
        var receivedBefore = _receivedEvents[^1];
        receivedBefore.Topic.ShouldBe(childTopic);

        // Act: navigate away and back
        // Unsubscribe from session A (this removes child topic subscriptions)
        await _hub.InvokeAsync("UnsubscribeFromSessionAsync", parentSessionA);

        // Subscribe to session B (navigate away)
        var snapshot2 = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionB);
        snapshot2.ValueKind.ShouldBe(JsonValueKind.Object);

        // Unsubscribe from B, re-subscribe to A
        await _hub.InvokeAsync("UnsubscribeFromSessionAsync", sessionB);
        var snapshot3 = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", parentSessionA);
        snapshot3.ValueKind.ShouldBe(JsonValueKind.Object);

        // Wait for the hub pump to be subscribed to the broadcaster again
        await WaitForBroadcasterSubscriberAsync();

        // Clear received events to isolate post-resubscribe behavior
        _receivedEvents.Clear();

        // Broadcast an event on child session C's topic
        var childMessagePayload2 = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg-child-after",
                role = "assistant",
                sessionID = childSessionC,
                time = new { created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            parts = new[]
            {
                new { type = "text", id = "part-child-after", sessionID = childSessionC, messageID = "msg-child-after", text = "After re-subscribe" }
            }
        });

        await broadcaster.BroadcastAsync(
            childTopic,
            "message.updated",
            childMessagePayload2,
            eventId: 102,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        // Assert: event from child session C IS received
        // THIS SHOULD FAIL because SubscribeToSessionAsync doesn't restore child topic subscriptions
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (_receivedEvents.Count < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        _receivedEvents.Count.ShouldBeGreaterThan(0,
            "Child session event should be received after re-subscribing to parent. " +
            "This test SHOULD FAIL (RED phase) because SubscribeToSessionAsync does not restore " +
            "child topic subscriptions from existing delegations.");
        var receivedAfter = _receivedEvents[^1];
        receivedAfter.Topic.ShouldBe(childTopic,
            "Event should be from child session topic");
        receivedAfter.Data.GetProperty("type").GetString().ShouldBe("message.updated");
    }

    private async Task<string> CreateSessionAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_server.ServerUrl) };
        var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        var rootPayload = JsonSerializer.Serialize(new { path = tempDir });
        await http.PostAsync("/api/workspace-roots",
            new StringContent(rootPayload, System.Text.Encoding.UTF8, "application/json"));

        var createPayload = JsonSerializer.Serialize(new
        {
            directory = tempDir,
            title = $"SignalR Delegation Test {Guid.NewGuid():N}",
            harnessType = "opencode"
        });
        var response = await http.PostAsync("/api/sessions",
            new StringContent(createPayload, System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

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
            // Check if we actually have events (defensive check)
            if (_receivedEvents.Count > 0)
            {
                return _receivedEvents[^1];
            }
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
