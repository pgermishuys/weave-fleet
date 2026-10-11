using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Infrastructure.Progress;
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
            lock (_receivedEvents)
            {
                _receivedEvents.Add(new ReceivedEvent(topic, eventId, data));
                _rawEvents.Add(data.GetRawText());
            }
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
    public async Task A_harnesses_running_work_reaches_the_session_and_the_status_bar_as_work_events()
    {
        await _hub.InvokeAsync("SubscribeToSessionsTopicAsync");
        await WaitForBroadcasterSubscriberAsync();
        var sessionId = await CreateSessionAsync();
        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Creating a session announces it on the sessions topic from the outbox, which can send it after create
        // returns, and the relay announces it idle when its pump starts (ResyncActivityStatusAsync), in either
        // order and late on a busy machine. Wait for both, so only what the harness's events caused is checked below.
        await WaitForWorkEventAsync("session_created", "sessions");
        await WaitForWorkEventAsync("activity_status", "sessions");
        var beforeWork = Received().Count;

        // What an adapter sends for a shell it moved to the background, then for its end: Fleet's own work.* events.
        var harness = await HarnessOfAsync(sessionId);
        await harness.PushEventAsync(Work(EventTypes.WorkStarted, sessionId, """
            {"workId":"sh_contract","kind":"shell","title":"shell","label":"bun run test:e2e","toolCallId":"call_bg",
             "background":true,"canStop":true,"canReadOutput":true}
            """));
        var started = await WaitForWorkEventAsync("work.started", $"session:{sessionId}");
        var startedOnSessions = await WaitForWorkEventAsync("work.started", "sessions");

        await harness.PushEventAsync(Work(EventTypes.WorkEnded, sessionId, """
            {"workId":"sh_contract","endedReason":"completed","detail":"exit 0"}
            """));
        var ended = await WaitForWorkEventAsync("work.ended", $"session:{sessionId}");

        // The exact shape the client's running-work list reads: RunningWorkItem, camelCase, nulls left out.
        var item = started.Data.GetProperty("properties");
        item.GetProperty("id").GetString().ShouldNotBeNullOrEmpty();
        item.GetProperty("sessionId").GetString().ShouldBe(sessionId);
        item.GetProperty("workId").GetString().ShouldBe("sh_contract");
        item.GetProperty("kind").GetString().ShouldBe("shell");
        item.GetProperty("title").GetString().ShouldBe("shell");
        item.GetProperty("label").GetString().ShouldBe("bun run test:e2e");
        item.GetProperty("status").GetString().ShouldBe("running");
        item.GetProperty("background").GetBoolean().ShouldBeTrue();
        item.GetProperty("toolCallId").GetString().ShouldBe("call_bg");
        item.GetProperty("canStop").GetBoolean().ShouldBeTrue();
        item.GetProperty("canReadOutput").GetBoolean().ShouldBeTrue();
        item.GetProperty("startedAt").GetString().ShouldNotBeNullOrEmpty();
        item.TryGetProperty("endedAt", out _).ShouldBeFalse(item.GetRawText());
        item.TryGetProperty("childSessionId", out _).ShouldBeFalse(item.GetRawText());
        startedOnSessions.Data.GetProperty("properties").GetProperty("id").GetString().ShouldBe(item.GetProperty("id").GetString());

        var end = ended.Data.GetProperty("properties");
        end.GetProperty("id").GetString().ShouldBe(item.GetProperty("id").GetString());
        end.GetProperty("status").GetString().ShouldBe("completed");
        end.GetProperty("endedReason").GetString().ShouldBe("completed");
        end.GetProperty("detail").GetString().ShouldBe("exit 0");
        end.GetProperty("endedAt").GetString().ShouldNotBeNullOrEmpty();

        // The harness's own events never reach the conversation as anything else.
        Received().Skip(beforeWork).Select(e => e.Data.GetProperty("type").GetString())
            .ShouldAllBe(type => type == "work.started" || type == "work.ended");

        // A session opened now gets the work in its snapshot, with its result, while it's recent.
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        var work = snapshot.GetProperty("runningWork").EnumerateArray().ShouldHaveSingleItem();
        work.GetProperty("workId").GetString().ShouldBe("sh_contract");
        work.GetProperty("endedReason").GetString().ShouldBe("completed");
        snapshot.GetProperty("delegations").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task How_full_the_context_is_reaches_the_session_as_context_updated_and_is_in_its_snapshot()
    {
        var sessionId = await CreateSessionAsync();
        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        // What an adapter sends after a model call of the session's: Fleet's own context.usage event.
        var harness = await HarnessOfAsync(sessionId);
        await harness.PushEventAsync(Work(EventTypes.ContextUsage, sessionId, """
            {"call":{"input":1000,"cacheRead":74500,"cacheWrite":0,"output":500,"reasoning":0},"limit":200000,"compactsAt":167000,
             "modelId":"claude-opus-5","providerId":"anthropic"}
            """));
        var updated = await WaitForWorkEventAsync("context.updated", $"session:{sessionId}");

        // The exact shape the ring by Send reads: SessionContextUsage, camelCase, nulls left out.
        var context = updated.Data.GetProperty("properties");
        context.GetProperty("sessionId").GetString().ShouldBe(sessionId);
        context.GetProperty("used").GetInt32().ShouldBe(76_000);
        context.GetProperty("limit").GetInt32().ShouldBe(200_000);
        context.GetProperty("compactsAt").GetInt32().ShouldBe(167_000);
        context.GetProperty("modelId").GetString().ShouldBe("claude-opus-5");
        context.GetProperty("lastCall").GetProperty("cacheRead").GetInt32().ShouldBe(74_500);
        context.GetProperty("lastCall").GetProperty("used").GetInt32().ShouldBe(76_000);
        context.GetProperty("compacting").GetBoolean().ShouldBeFalse();
        context.GetProperty("turns").GetArrayLength().ShouldBe(0);
        context.TryGetProperty("compactedAt", out _).ShouldBeFalse(context.GetRawText());

        // A compaction starting says so.
        await harness.PushEventAsync(Work(EventTypes.ContextCompaction, sessionId, """{"phase":"started","trigger":"manual"}"""));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Received().Any(e => e.Data.GetProperty("type").GetString() == "context.updated"
            && e.Data.GetProperty("properties").GetProperty("compacting").GetBoolean()))
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "No context.updated said the compaction started.");
            await _eventReceived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }

        // The harness's own context events never reach the conversation.
        Received().Select(e => e.Data.GetProperty("type").GetString()).ShouldAllBe(type => type == "context.updated");

        // A session opened now gets it in its snapshot.
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        snapshot.GetProperty("context").GetProperty("used").GetInt32().ShouldBe(76_000);
        snapshot.GetProperty("context").GetProperty("compacting").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_turn_a_usage_limit_stopped_reaches_the_session_with_its_reset_and_when_Fleet_tries_again()
    {
        var sessionId = await CreateSessionAsync();
        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        // OpenCode's shape: the provider's status and words under data.
        var harness = await HarnessOfAsync(sessionId);
        await harness.PushEventAsync(Work(EventTypes.SessionError, sessionId, """
            {"error":{"name":"APIError","data":{"message":"You've hit your session limit · resets 3:43am (UTC)","statusCode":429,"isRetryable":false}}}
            """));

        var failed = await WaitForWorkEventAsync("turn.failed", $"session:{sessionId}");
        var error = failed.Data.GetProperty("properties").GetProperty("error");
        error.GetProperty("kind").GetString().ShouldBe(TurnErrorKinds.UsageLimit);
        var retryAt = DateTimeOffset.Parse(error.GetProperty("retryAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        retryAt.ToUniversalTime().TimeOfDay.ShouldBe(new TimeSpan(3, 43, 0));

        // Fleet schedules the retry and says when, the shape the failure card reads.
        var scheduled = await WaitForWorkEventAsync("session.retry", $"session:{sessionId}");
        var retry = scheduled.Data.GetProperty("properties");
        retry.GetProperty("sessionId").GetString().ShouldBe(sessionId);
        retry.GetProperty("retry").GetProperty("kind").GetString().ShouldBe(TurnErrorKinds.UsageLimit);
        retry.GetProperty("retry").GetProperty("attempt").GetInt32().ShouldBe(1);
        retry.GetProperty("retry").GetProperty("providerSaid").GetBoolean().ShouldBeTrue();
        DateTimeOffset.Parse(retry.GetProperty("retry").GetProperty("dueAt").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(retryAt + TurnRetryPolicy.AfterReset);
    }

    [Fact]
    public async Task Usage_limits_reach_the_users_clients_as_harness_usage_and_stay_readable()
    {
        var sessionId = await CreateSessionAsync();
        await _hub.InvokeAsync("SubscribeToSessionsTopicAsync");
        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        // What an adapter sends when its harness says how much of its account's limits are used.
        var harness = await HarnessOfAsync(sessionId);
        await harness.PushEventAsync(Work(EventTypes.HarnessUsage, sessionId, """
            {"windows":[{"window":"five_hour","utilization":0.82,"resetsAt":"2099-10-07T14:05:00+00:00","status":"warning"},
                        {"window":"seven_day","utilization":0.63,"resetsAt":"2099-10-12T09:00:00+00:00","status":"allowed"}]}
            """));
        var received = await WaitForWorkEventAsync("harness.usage", "sessions");

        // The exact shape the ring's card and the status-bar chip read: HarnessUsage, camelCase.
        var usage = received.Data.GetProperty("properties");
        usage.GetProperty("harnessType").GetString().ShouldBe("opencode");
        var windows = usage.GetProperty("windows");
        windows.GetArrayLength().ShouldBe(2);
        windows[0].GetProperty("window").GetString().ShouldBe("five_hour");
        windows[0].GetProperty("utilization").GetDouble().ShouldBe(0.82);
        windows[0].GetProperty("resetsAt").GetDateTimeOffset().ShouldBe(new DateTimeOffset(2099, 10, 7, 14, 5, 0, TimeSpan.Zero));
        windows[0].GetProperty("status").GetString().ShouldBe("warning");
        usage.TryGetProperty("updatedAt", out _).ShouldBeTrue();

        // The account's, not the conversation's: nothing on the session's topic.
        Received().Where(e => e.Topic == $"session:{sessionId}").ShouldBeEmpty();

        // A page opened now reads them.
        using var http = new HttpClient { BaseAddress = new Uri(_server.ServerUrl) };
        var listed = JsonDocument.Parse(await http.GetStringAsync("/api/harnesses/usage")).RootElement;
        listed.GetArrayLength().ShouldBe(1);
        listed[0].GetProperty("harnessType").GetString().ShouldBe("opencode");
        listed[0].GetProperty("windows").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task A_retry_reaches_the_users_clients_with_the_attempt_out_of_how_many_why_and_when()
    {
        var sessionId = await CreateSessionAsync();
        await _hub.InvokeAsync("SubscribeToSessionsTopicAsync");
        await WaitForBroadcasterSubscriberAsync();

        // What Claude Code's api_retry becomes.
        var harness = await HarnessOfAsync(sessionId);
        await harness.PushEventAsync(Work(EventTypes.SessionStatus, sessionId,
            """{"status":{"type":"retry","count":3,"max":10,"reason":"API overloaded (529)","delay":12000}}"""));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        ReceivedEvent? status;
        while ((status = Received().FirstOrDefault(e => e.Topic == "sessions"
                   && e.Data.GetProperty("type").GetString() == "activity_status"
                   && e.Data.GetProperty("properties").GetProperty("activityStatus").GetString() == "retry")) is null)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "No activity_status said it was retrying.");
            await _eventReceived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }

        var props = status.Data.GetProperty("properties");
        props.GetProperty("sessionId").GetString().ShouldBe(sessionId);
        props.GetProperty("attempt").GetInt32().ShouldBe(3);
        props.GetProperty("maxAttempts").GetInt32().ShouldBe(10);
        props.GetProperty("message").GetString().ShouldBe("API overloaded (529)");
        (props.GetProperty("next").GetDateTimeOffset() - DateTimeOffset.UtcNow).TotalSeconds.ShouldBeInRange(1, 13);
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
    public async Task A_change_to_the_users_mods_reaches_their_clients_as_mods_changed()
    {
        await _hub.InvokeAsync("SubscribeToSessionsTopicAsync");
        await WaitForBroadcasterSubscriberAsync();

        // "Start without mods" is a change every client refetches /api/mods for, like Keep, Undo and on/off.
        using (var scope = _server.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WeaveFleet.Application.Mods.ModService>().SetSafeModeAsync(true);

        var received = await WaitForWorkEventAsync("mods.changed", "sessions");
        var props = received.Data.GetProperty("properties");
        props.GetProperty("reason").GetString().ShouldBe("safe-mode");
        (props.TryGetProperty("name", out var name) ? name.ValueKind : JsonValueKind.Null).ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_session_notification_carries_its_kind_request_and_machine()
    {
        await _hub.InvokeAsync("SubscribeToSessionsTopicAsync");
        var sink = _server.Services.GetServices<WeaveFleet.Application.Sessions.ISessionNotificationSink>()
            .OfType<WeaveFleet.Application.Sessions.BroadcastNotificationSink>()
            .Single();

        await sink.HandleAsync(new SessionNotificationPayload
        {
            SessionId = "s1",
            Reason = SessionNotificationReasons.NeedsYou,
            Kind = SessionNotificationKinds.Permission,
            RequestId = "perm-1",
            MachineId = "hangar-id",
            MachineName = "hangar",
            Title = "Fix flaky SignalR reconnect test",
            Body = "Wants to run dotnet test",
        }, "local-user", CancellationToken.None);

        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No session_notification arrived");
        received.Topic.ShouldBe("sessions");
        received.Data.GetProperty("type").GetString().ShouldBe("session_notification");
        var props = received.Data.GetProperty("properties");
        props.GetProperty("sessionId").GetString().ShouldBe("s1");
        props.GetProperty("reason").GetString().ShouldBe("needs_you");
        props.GetProperty("kind").GetString().ShouldBe("permission");
        props.GetProperty("requestId").GetString().ShouldBe("perm-1");
        props.GetProperty("machineId").GetString().ShouldBe("hangar-id");
        props.GetProperty("machineName").GetString().ShouldBe("hangar");
        props.GetProperty("title").GetString().ShouldBe("Fix flaky SignalR reconnect test");
        props.GetProperty("body").GetString().ShouldBe("Wants to run dotnet test");
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
    public async Task Hub_sends_domain_event_type_when_DomainEvent_is_attached()
    {
        // Arrange: when the event pipeline translates a raw harness event (e.g. "session.status")
        // into a domain event (e.g. TurnStarted), the hub must send the domain event type on the wire
        // so that the client reducer can match on "turn.started" rather than "session.status".
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();

        // Simulate what InProcessFanOutService does: broadcast raw type "session.status"
        // but attach a TurnStarted domain event.
        var rawPayload = JsonSerializer.SerializeToElement(new { status = "busy" });
        var domainEvent = new WeaveFleet.Domain.Events.TurnStarted
        {
            Payload = new WeaveFleet.Domain.Events.TurnStartedPayload
            {
                SessionId = sessionId,
                MessageId = "msg-1",
                Index = 0,
                Agent = "default",
            }
        };

        await broadcaster.BroadcastAsync(
            topic,
            "session.status",   // raw harness type
            rawPayload,
            eventId: null,
            domainEvent: domainEvent,
            userId: "local-user",
            ct: CancellationToken.None);

        // Assert: the client must receive "turn.started", NOT "session.status"
        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No event received");

        var wireType = received.Data.GetProperty("type").GetString();
        wireType.ShouldBe("turn.started",
            $"Hub sent raw harness type '{wireType}' instead of domain event type 'turn.started'. " +
            $"The client reducer handles 'turn.started' — sending 'session.status' means the dots never appear. " +
            $"Full event: {received.Data.GetRawText()}");
    }

    /// <summary>Matches the camelCase policy the fan-out service serialises domain payloads with.</summary>
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Hub_sends_domain_event_type_for_turn_failed()
    {
        // session.error (raw) → turn.failed (domain). Without the mapping the hub falls back to the
        // CLR type name ("TurnFailed"), which the client reducer does not handle, so a failed turn
        // would silently look like a turn that simply finished.
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();

        // Serialised the way InProcessFanOutService serialises it: camelCase, so the client sees
        // `error.message`, not `Error.Message`.
        var payload = JsonSerializer.SerializeToElement(new WeaveFleet.Domain.Events.TurnFailedPayload
        {
            SessionId = sessionId,
            MessageId = "msg-1",
            Error = new WeaveFleet.Domain.Events.TurnError
            {
                Name = "APIError",
                Message = "Overloaded",
                IsRetryable = true,
            },
        }, WebJson);

        var domainEvent = new WeaveFleet.Domain.Events.TurnFailed
        {
            Payload = new WeaveFleet.Domain.Events.TurnFailedPayload
            {
                SessionId = sessionId,
                MessageId = "msg-1",
                Error = new WeaveFleet.Domain.Events.TurnError
                {
                    Name = "APIError",
                    Message = "Overloaded",
                    IsRetryable = true,
                },
            }
        };

        await broadcaster.BroadcastAsync(
            topic,
            "session.error",   // raw harness type
            payload,
            eventId: null,
            domainEvent: domainEvent,
            userId: "local-user",
            ct: CancellationToken.None);

        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No event received");

        var wireType = received.Data.GetProperty("type").GetString();
        wireType.ShouldBe("turn.failed",
            $"Hub sent '{wireType}' instead of 'turn.failed'. The client reducer handles 'turn.failed' — " +
            $"anything else means a failed turn goes idle with no explanation. Full event: {received.Data.GetRawText()}");

        var properties = received.Data.GetProperty("properties");
        properties.GetProperty("error").GetProperty("message").GetString().ShouldBe("Overloaded");
    }

    [Fact]
    public async Task Hub_sends_domain_event_type_for_session_idled()
    {
        // session.idle (raw) → session.idled (domain)
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();

        var rawPayload = JsonSerializer.SerializeToElement(new { status = "idle" });
        var domainEvent = new WeaveFleet.Domain.Events.SessionIdled
        {
            Payload = new WeaveFleet.Domain.Events.SessionIdledPayload
            {
                SessionId = sessionId,
            }
        };

        await broadcaster.BroadcastAsync(
            topic,
            "session.idle",
            rawPayload,
            eventId: null,
            domainEvent: domainEvent,
            userId: "local-user",
            ct: CancellationToken.None);

        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No event received");

        var wireType = received.Data.GetProperty("type").GetString();
        wireType.ShouldBe("session.idled",
            $"Hub sent raw type '{wireType}' instead of domain type 'session.idled'. " +
            $"Full event: {received.Data.GetRawText()}");
    }

    [Fact]
    public async Task Hub_sends_domain_event_type_for_message_part_delta_streamed()
    {
        // message.part.delta (raw) → message.part.delta.streamed (domain)
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();

        var rawPayload = JsonSerializer.SerializeToElement(new
        {
            sessionID = sessionId,
            messageID = "msg-1",
            partID = "part-1",
            field = "text",
            delta = "Hello "
        });
        var domainEvent = new WeaveFleet.Domain.Events.MessagePartDeltaStreamed
        {
            Payload = new WeaveFleet.Domain.Events.MessagePartDeltaStreamedPayload
            {
                SessionId = sessionId,
                MessageId = "msg-1",
                PartId = "part-1",
                Field = "text",
                Delta = "Hello "
            }
        };

        await broadcaster.BroadcastAsync(
            topic,
            "message.part.delta",
            rawPayload,
            eventId: null,
            domainEvent: domainEvent,
            userId: "local-user",
            ct: CancellationToken.None);

        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No event received");

        var wireType = received.Data.GetProperty("type").GetString();
        wireType.ShouldBe("message.part.delta.streamed",
            $"Hub sent raw type '{wireType}' instead of domain type 'message.part.delta.streamed'. " +
            $"Full event: {received.Data.GetRawText()}");
    }

    [Fact]
    public async Task Hub_preserves_raw_type_when_no_DomainEvent_is_attached()
    {
        // When no DomainEvent is attached, the hub should send the raw type as-is.
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();

        var payload = JsonSerializer.SerializeToElement(new { activityStatus = "busy" });
        await broadcaster.BroadcastAsync(
            topic,
            "activity_status",
            payload,
            eventId: 10,
            domainEvent: null,
            userId: "local-user",
            ct: CancellationToken.None);

        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No event received");

        received.Data.GetProperty("type").GetString().ShouldBe("activity_status");
    }

    [Fact]
    public async Task Hub_sends_files_changed_domain_event_with_correct_shape()
    {
        // Arrange: create a session and subscribe
        var sessionId = await CreateSessionAsync();
        var topic = $"session:{sessionId}";

        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        var broadcaster = _server.Services.GetRequiredService<IEventBroadcaster>();

        // Act: broadcast a FilesChanged domain event
        var domainEvent = new WeaveFleet.Domain.Events.FilesChanged
        {
            Payload = new WeaveFleet.Domain.Events.FilesChangedPayload
            {
                SessionId = sessionId,
                Files = new[]
                {
                    new WeaveFleet.Domain.Events.FileChangeEntry
                    {
                        Path = "src/Example.cs",
                        ChangeType = "modified"
                    },
                    new WeaveFleet.Domain.Events.FileChangeEntry
                    {
                        Path = "tests/ExampleTests.cs",
                        ChangeType = "created"
                    }
                }
            }
        };

        var rawPayload = JsonSerializer.SerializeToElement(new
        {
            sessionId,
            files = new[]
            {
                new { path = "src/Example.cs", changeType = "modified" },
                new { path = "tests/ExampleTests.cs", changeType = "created" }
            }
        });

        await broadcaster.BroadcastAsync(
            topic,
            "files.changed",
            rawPayload,
            eventId: 500,
            domainEvent: domainEvent,
            userId: "local-user",
            ct: CancellationToken.None);

        // Assert: the client receives the event with correct shape
        var received = await WaitForEventAsync(TimeSpan.FromSeconds(5));
        received.ShouldNotBeNull("No files.changed event received");

        // Verify event type discriminator
        received.Data.TryGetProperty("type", out var typeEl).ShouldBeTrue(
            $"Missing 'type'. Actual: {received.Data.GetRawText()}");
        typeEl.GetString().ShouldBe("files.changed",
            "Event type discriminator should be 'files.changed'");

        // Verify properties structure
        received.Data.TryGetProperty("properties", out var props).ShouldBeTrue(
            $"Missing 'properties'. Actual: {received.Data.GetRawText()}");

        // Verify sessionId
        props.TryGetProperty("sessionId", out var sessionIdProp).ShouldBeTrue(
            $"Missing 'sessionId' in properties. Actual: {props.GetRawText()}");
        sessionIdProp.GetString().ShouldBe(sessionId);

        // Verify files array
        props.TryGetProperty("files", out var filesProp).ShouldBeTrue(
            $"Missing 'files' array in properties. Actual: {props.GetRawText()}");
        filesProp.ValueKind.ShouldBe(JsonValueKind.Array,
            "Files should be an array");

        var filesArray = filesProp.EnumerateArray().ToList();
        filesArray.Count.ShouldBe(2, "Expected 2 file change entries");

        // Verify first file entry
        var firstFile = filesArray[0];
        firstFile.TryGetProperty("path", out var path1).ShouldBeTrue(
            $"First file missing 'path'. Actual: {firstFile.GetRawText()}");
        path1.GetString().ShouldBe("src/Example.cs");

        firstFile.TryGetProperty("changeType", out var changeType1).ShouldBeTrue(
            $"First file missing 'changeType'. Actual: {firstFile.GetRawText()}");
        changeType1.GetString().ShouldBe("modified");

        // Verify second file entry
        var secondFile = filesArray[1];
        secondFile.TryGetProperty("path", out var path2).ShouldBeTrue(
            $"Second file missing 'path'. Actual: {secondFile.GetRawText()}");
        path2.GetString().ShouldBe("tests/ExampleTests.cs");

        secondFile.TryGetProperty("changeType", out var changeType2).ShouldBeTrue(
            $"Second file missing 'changeType'. Actual: {secondFile.GetRawText()}");
        changeType2.GetString().ShouldBe("created");
    }

    [Fact]
    public async Task Hub_sends_canvas_events_with_the_exact_wire_shape()
    {
        var sessionId = await CreateSessionAsync();
        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        // Act: the agent opens a diagram, then the user moves a box
        using var scope = _server.Services.CreateScope();
        var canvases = scope.ServiceProvider.GetRequiredService<ICanvasService>();
        var opened = await canvases.OpenAsync(
            sessionId,
            CanvasKinds.Diagram,
            "Session event flow",
            JsonNode.Parse("""
                {
                  "nodes": [{ "id": "n1", "label": "NuCode session", "detail": "NuCode/Sessions" }, { "id": "n2", "label": "SessionEventsHub" }],
                  "edges": [{ "id": "e1", "from": "n1", "to": "n2", "label": "publishes" }]
                }
                """));
        opened.IsSuccess.ShouldBeTrue(opened.Error?.Message);
        var canvasId = opened.Value.Canvas.Id;
        var moved = await canvases.ApplyAsync(sessionId, canvasId, CanvasActor.User, JsonNode.Parse("""[{"op":"moveNode","id":"n1","x":20,"y":44.5}]"""));
        moved.IsSuccess.ShouldBeTrue(moved.Error?.Message);

        // Assert: canvas.updated (open), canvas.focused, canvas.updated (move), exactly as the client receives them
        // Read the received list rather than WaitForEventAsync, which returns the latest event and can
        // skip one when two arrive together.
        List<ReceivedEvent> canvasEvents = [];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            canvasEvents = _receivedEvents.ToArray()
                .Where(e => e.Data.GetProperty("type").GetString()?.StartsWith("canvas.", StringComparison.Ordinal) == true)
                .ToList();
            if (canvasEvents.Count >= 3)
                break;
            await _eventReceived.WaitAsync(TimeSpan.FromMilliseconds(100));
        }

        canvasEvents.Count.ShouldBe(3, $"Raw events: {string.Join("; ", _rawEvents)}");
        canvasEvents.ShouldAllBe(e => e.Topic == $"session:{sessionId}");
        canvasEvents[1].Data.GetRawText().ShouldBe(
            $$$"""{"type":"canvas.focused","eventId":null,"properties":{"sessionId":"{{{sessionId}}}","canvasId":"{{{canvasId}}}"}}""");
        canvasEvents[2].Data.GetRawText().ShouldBe(
            $$$"""{"type":"canvas.updated","eventId":null,"properties":{"sessionId":"{{{sessionId}}}","canvasId":"{{{canvasId}}}","kind":"diagram","title":"Session event flow","version":2,"actor":"user","state":{"direction":"TB","nodes":[{"id":"n1","label":"NuCode session","detail":"NuCode/Sessions","x":20,"y":44.5,"placedByUser":true},{"id":"n2","label":"SessionEventsHub"}],"edges":[{"id":"e1","from":"n1","to":"n2","label":"publishes","style":"solid"}]},"summary":"1 box moved"}}""");
        canvasEvents[0].Data.GetProperty("properties").GetProperty("summary").GetString().ShouldBe("+2 boxes, +1 edge");
    }

    [Fact]
    public async Task Hub_sends_session_progress_to_the_row_and_the_open_session_with_the_exact_wire_shape()
    {
        var sessionId = await CreateSessionAsync();
        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await _hub.InvokeAsync("SubscribeToSessionsTopicAsync");
        await WaitForBroadcasterSubscriberAsync();

        // Act: a todo list arrives, as the relay would hand it over after translating a harness event
        var observer = _server.Services.GetRequiredService<SessionProgressObserver>();
        observer.Observe(sessionId, "local-user", new TodosReported
        {
            Payload = new TodosReportedPayload
            {
                SessionId = sessionId,
                Items =
                [
                    new TodoEntry { Content = "Write the migration", Status = TodoStatuses.Completed, Priority = "high" },
                    new TodoEntry { Content = "Drop the indexes", Status = TodoStatuses.InProgress },
                ],
            },
        });

        // Assert: the row summary on "sessions" and the full detail on the session's topic
        List<ReceivedEvent> progressEvents = [];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            progressEvents = _receivedEvents.ToArray()
                .Where(e => e.Data.GetProperty("type").GetString() is "session_progress" or "progress.updated")
                .ToList();
            if (progressEvents.Count >= 2)
                break;
            await _eventReceived.WaitAsync(TimeSpan.FromMilliseconds(100));
        }

        progressEvents.Count.ShouldBe(2, $"Raw events: {string.Join("; ", _rawEvents)}");
        var row = progressEvents.Single(e => e.Topic == "sessions");
        row.Data.GetRawText().ShouldBe(
            $$$"""{"type":"session_progress","eventId":null,"properties":{"sessionId":"{{{sessionId}}}","kind":"todos","done":1,"total":2,"current":"Drop the indexes"}}""");

        var detail = progressEvents.Single(e => e.Topic == $"session:{sessionId}");
        var updatedAt = detail.Data.GetProperty("properties").GetProperty("updatedAt").GetString();
        DateTimeOffset.TryParse(updatedAt, out _).ShouldBeTrue();
        detail.Data.GetRawText().ShouldBe(
            $$$"""{"type":"progress.updated","eventId":null,"properties":{"sessionId":"{{{sessionId}}}","kind":"todos","done":1,"total":2,"current":"Drop the indexes","todos":[{"content":"Write the migration","status":"completed","priority":"high"},{"content":"Drop the indexes","status":"in_progress"}],"updatedAt":"{{{updatedAt}}}","subagents":[]}}""");
    }

    [Fact]
    public async Task Hub_sends_app_updated_with_the_exact_wire_shape()
    {
        if (OperatingSystem.IsWindows())
            return;

        var sessionId = await CreateSessionAsync();
        await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        await WaitForBroadcasterSubscriberAsync();

        // Act: run a command that exits at once
        using var scope = _server.Services.CreateScope();
        var started = await scope.ServiceProvider.GetRequiredService<AppRunService>().StartAsync(sessionId, "echo hi; exit 0");
        started.App.ShouldNotBeNull(started.Problem);
        var appId = started.App.Id;

        // Assert: app.updated for the start and the exit; the last is the run as it ended
        List<ReceivedEvent> appEvents = [];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            appEvents = _receivedEvents.ToArray()
                .Where(e => e.Data.GetProperty("type").GetString() == "app.updated")
                .ToList();
            if (appEvents.Any(e => e.Data.GetProperty("properties").GetProperty("reason").GetString() == "exited"))
                break;
            await _eventReceived.WaitAsync(TimeSpan.FromMilliseconds(100));
        }

        appEvents.Count.ShouldBe(2, $"Raw events: {string.Join("; ", _rawEvents)}");
        appEvents.ShouldAllBe(e => e.Topic == $"session:{sessionId}");
        appEvents[0].Data.GetProperty("properties").GetProperty("reason").GetString().ShouldBe("started");
        appEvents[1].Data.GetRawText().ShouldBe(
            $$$"""{"type":"app.updated","eventId":null,"properties":{"sessionId":"{{{sessionId}}}","appId":"{{{appId}}}","command":"echo hi; exit 0","status":"exited","ports":[],"exitCode":0,"reason":"exited"}}""");
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

    [Fact(Skip = "Buffers removed - test obsolete")]
    public async Task Snapshot_includes_buffered_tool_part_before_persistence()
    {
        // This test directly populates the buffers (simulating the window between
        // InProcessFanOutService buffering and HarnessEventPersistenceService clearing)
        // and verifies that BuildAtomicSnapshotAsync merges them into the snapshot.

        var sessionId = await CreateSessionAsync();
        var messageId = $"msg-{Guid.NewGuid():N}";
        var textPartId = $"part-text-{Guid.NewGuid():N}";
        var toolPartId = $"part-tool-{Guid.NewGuid():N}";
        var toolCallId = $"call-{Guid.NewGuid():N}";

        // Directly populate buffers (singletons) to simulate in-flight state
        // var partBuffer = _server.Services.GetRequiredService<MessagePartBuffer>();
        // var snapshotBuffer = _server.Services.GetRequiredService<MessageSnapshotBuffer>();

        // Buffer the message (as if message.created was just broadcast but not persisted)
        var bufferedMessage = new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = messageId,
                Role = "assistant",
                SessionId = sessionId,
                Time = new MessageEventTime { Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
            },
            Parts =
            [
                new TextMessageEventPart
                {
                    Id = textPartId,
                    SessionId = sessionId,
                    MessageId = messageId,
                    Text = "Thinking..."
                }
            ]
        };
        // snapshotBuffer.Set(sessionId, messageId, bufferedMessage);

        // Buffer the tool part (as if message.part.updated was just broadcast but not persisted)
        var toolPart = new ToolMessageEventPart
        {
            Id = toolPartId,
            SessionId = sessionId,
            MessageId = messageId,
            ToolName = "bash",
            CallId = toolCallId,
            State = new ToolRunningState
            {
                Input = JsonSerializer.SerializeToElement(new { command = "echo test" })
            }
        };
        // partBuffer.Set(sessionId, messageId, toolPartId, toolPart);

        // Act: subscribe — BuildAtomicSnapshotAsync should merge both buffers
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        // Assert: snapshot contains the message with both parts
        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue(
            $"Snapshot missing 'messages'. Actual: {snapshot.GetRawText()}");

        var messageArray = messages.EnumerateArray().ToList();
        messageArray.Count.ShouldBeGreaterThan(0, "Expected at least one message in snapshot");

        var messageWithTool = messageArray.FirstOrDefault(m =>
            m.TryGetProperty("info", out var info) &&
            info.TryGetProperty("id", out var id) &&
            id.GetString() == messageId);

        messageWithTool.ValueKind.ShouldNotBe(JsonValueKind.Undefined,
            $"Expected snapshot to contain message {messageId}. " +
            $"Messages in snapshot: {messages.GetRawText()}");

        messageWithTool.TryGetProperty("parts", out var messageParts).ShouldBeTrue();
        var partsArray = messageParts.EnumerateArray().ToList();
        partsArray.Count.ShouldBe(2,
            $"Expected 2 parts (text + tool) but got {partsArray.Count}. Parts: {messageParts.GetRawText()}");

        // Verify the tool part is present with correct properties
        var toolPartElement = partsArray.FirstOrDefault(p =>
            p.TryGetProperty("id", out var id) && id.GetString() == toolPartId);

        toolPartElement.ValueKind.ShouldNotBe(JsonValueKind.Undefined,
            $"Expected to find tool part {toolPartId} in message. Parts: {messageParts.GetRawText()}");

        toolPartElement.TryGetProperty("tool", out var toolName).ShouldBeTrue(
            $"Tool part missing 'tool' field. Actual: {toolPartElement.GetRawText()}");
        toolName.GetString().ShouldBe("bash");

        toolPartElement.TryGetProperty("callID", out var callIdProp).ShouldBeTrue(
            $"Tool part missing 'callID' field. Actual: {toolPartElement.GetRawText()}");
        callIdProp.GetString().ShouldBe(toolCallId);

        toolPartElement.TryGetProperty("state", out var state).ShouldBeTrue(
            $"Tool part missing 'state' field. Actual: {toolPartElement.GetRawText()}");
        state.TryGetProperty("input", out var input).ShouldBeTrue(
            $"Tool state missing 'input' field. Actual: {state.GetRawText()}");
    }

    [Fact(Skip = "Buffers removed - test obsolete")]
    public async Task Snapshot_includes_in_flight_tool_part_on_resubscribe()
    {
        // Verifies that re-subscribing merges buffered parts onto persisted messages.
        // First subscribe returns empty, then we persist a message and buffer a tool part,
        // and re-subscribe should show the persisted message with the buffered tool part merged in.

        var sessionId = await CreateSessionAsync();
        var messageId = $"msg-{Guid.NewGuid():N}";
        var toolPartId = $"part-tool-{Guid.NewGuid():N}";
        var toolCallId = $"call-{Guid.NewGuid():N}";

        // Step 1: Initial subscribe (baseline — empty)
        var initialSnapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);
        initialSnapshot.TryGetProperty("messages", out var initialMessages).ShouldBeTrue();
        initialMessages.GetArrayLength().ShouldBe(0, "Expected empty message list for new session");

        // Step 2: Persist a message via the repository (simulates projection completing)
        await PersistMessageWithCompletedToolAsync(sessionId);

        // Step 3: Buffer a NEW tool part onto that persisted message
        // We need the persisted message's ID — use the repo to find it
        var messageRepo = _server.Services.GetRequiredService<IMessageRepository>();
        var persistedMessages = await messageRepo.GetBySessionAsync(sessionId, 100, null);
        persistedMessages.Count.ShouldBeGreaterThan(0, "Expected at least one persisted message");
        var persistedMsgId = persistedMessages[0].Id;

        // var partBuffer = _server.Services.GetRequiredService<MessagePartBuffer>();
        var toolPart = new ToolMessageEventPart
        {
            Id = toolPartId,
            SessionId = sessionId,
            MessageId = persistedMsgId,
            ToolName = "read_file",
            CallId = toolCallId,
            State = new ToolRunningState
            {
                Input = JsonSerializer.SerializeToElement(new { path = "/tmp/test.txt" })
            }
        };
        // partBuffer.Set(sessionId, persistedMsgId, toolPartId, toolPart);

        // Act: re-subscribe
        var snapshot = await _hub.InvokeAsync<JsonElement>("SubscribeToSessionAsync", sessionId);

        snapshot.TryGetProperty("messages", out var messages).ShouldBeTrue(
            $"Snapshot missing 'messages'. Actual: {snapshot.GetRawText()}");

        var messageArray = messages.EnumerateArray().ToList();
        messageArray.Count.ShouldBeGreaterThan(0, "Expected at least one message in snapshot");

        // Find the persisted message
        var targetMsg = messageArray.FirstOrDefault(m =>
            m.TryGetProperty("info", out var info) &&
            info.TryGetProperty("id", out var id) &&
            id.GetString() == persistedMsgId);

        targetMsg.ValueKind.ShouldNotBe(JsonValueKind.Undefined,
            $"Expected message {persistedMsgId} in snapshot. Messages: {messages.GetRawText()}");

        // The persisted message should now include the buffered tool part
        targetMsg.TryGetProperty("parts", out var parts).ShouldBeTrue();
        var toolPartElement = parts.EnumerateArray().FirstOrDefault(p =>
            p.TryGetProperty("id", out var id) && id.GetString() == toolPartId);

        toolPartElement.ValueKind.ShouldNotBe(JsonValueKind.Undefined,
            $"Expected tool part {toolPartId} merged into persisted message. Parts: {parts.GetRawText()}");

        toolPartElement.TryGetProperty("tool", out var toolName).ShouldBeTrue();
        toolName.GetString().ShouldBe("read_file");
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

    /// <summary>The harness behind <paramref name="sessionId"/>, which can push events as a real adapter would.</summary>
    private async Task<WeaveFleet.TestHarness.TestHarnessSession> HarnessOfAsync(string sessionId)
    {
        using var scope = _server.Services.CreateScope();
        var session = await scope.ServiceProvider.GetRequiredService<ISessionRepository>().GetByIdAsync(sessionId);
        var instance = _server.Services.GetRequiredService<InstanceTracker>().Get(session.ShouldNotBeNull().InstanceId);
        return instance.ShouldBeOfType<WeaveFleet.TestHarness.TestHarnessSession>();
    }

    private static HarnessEvent Work(string type, string sessionId, string report) => new()
    {
        Type = type,
        SessionId = sessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonDocument.Parse(report).RootElement.Clone(),
    };

    /// <summary>Waits for a <paramref name="type"/> event on <paramref name="topic"/>; work is recorded off the relay, so others may come first.</summary>
    private async Task<ReceivedEvent> WaitForWorkEventAsync(string type, string topic)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var found = Received().FirstOrDefault(e => e.Topic == topic && e.Data.GetProperty("type").GetString() == type);
            if (found is not null)
                return found;
            await _eventReceived.WaitAsync(TimeSpan.FromMilliseconds(200));
        }

        lock (_receivedEvents)
            throw new ShouldAssertException($"No {type} on {topic}. Received: {string.Join("; ", _rawEvents)}");
    }

    /// <summary>The events received so far, copied: the client adds to the list on its own thread.</summary>
    private List<ReceivedEvent> Received()
    {
        lock (_receivedEvents)
            return [.. _receivedEvents];
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
            // No Bun for the mod host, whatever this machine has installed: tests never start a real one here.
            builder.ConfigureTestServices(services => services.AddSingleton<WeaveFleet.Application.Mods.Host.IModHostBun, WeaveFleet.Testing.Fakes.NoModHostBun>());
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
