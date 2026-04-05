using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class HarnessEventRelayTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (
        IEventBroadcaster Broadcaster,
        ISessionRepository SessionRepo,
        IServiceScopeFactory ScopeFactory,
        TaskCompletionSource<(string Topic, string Type)> BroadcastSignal
    ) BuildDependencies()
    {
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var sessionRepo = Substitute.For<ISessionRepository>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        serviceProvider.GetService(typeof(ISessionRepository)).Returns(sessionRepo);
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        var broadcastSignal = new TaskCompletionSource<(string, string)>();
        broadcaster
            .When(b => b.BroadcastAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>()))
            .Do(call => broadcastSignal.TrySetResult(
                (call.ArgAt<string>(0), call.ArgAt<string>(1))));

        return (broadcaster, sessionRepo, scopeFactory, broadcastSignal);
    }

    // -----------------------------------------------------------------------
    // Test 1: Happy path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Events_are_broadcast_on_fleet_session_topic()
    {
        var (broadcaster, sessionRepo, scopeFactory, broadcastSignal) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = new HarnessEventRelay(
            tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

        var fleetSessionId = "fleet-session-123";
        var instanceId = "instance-abc";

        var session = new Session { Id = fleetSessionId, InstanceId = instanceId };
        sessionRepo.GetAnyForInstanceAsync(instanceId).Returns(session);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);

        // Give ExecuteAsync time to wire event handlers
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        // Emit an event and complete the stream
        var evt = new HarnessEvent
        {
            Type = "message.updated",
            SessionId = "oc-session-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { text = "hello" })
        };
        instance.Emit(evt);
        instance.Complete();

        // Wait for broadcast
        var result = await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal($"session:{fleetSessionId}", result.Topic);
        Assert.Equal("message.updated", result.Type);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Test 2: Instance removed cancels subscription
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Removing_instance_cancels_its_subscription()
    {
        var (broadcaster, sessionRepo, scopeFactory, _) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = new HarnessEventRelay(
            tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

        var instanceId = "instance-xyz";
        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = "fleet-session-xyz", InstanceId = instanceId });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        // Instance that blocks indefinitely (no events, no completion)
        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        // Let pump start
        await Task.Delay(100);

        // Remove the instance — should cancel its subscription
        tracker.Remove(instanceId);

        // Instance's SubscribeAsync should be cancelled via the pump's CT.
        // Wait briefly then verify no events were broadcast
        await Task.Delay(200);

        await broadcaster.DidNotReceive().BroadcastAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<object>(), Arg.Any<CancellationToken>());

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Test 3: Session lookup not found — no events are broadcast
    // -----------------------------------------------------------------------

    [Fact]
    public async Task No_events_broadcast_when_session_lookup_always_fails()
    {
        var (broadcaster, sessionRepo, scopeFactory, _) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = new HarnessEventRelay(
            tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

        var instanceId = "instance-no-session";

        // Session repo always returns null
        sessionRepo.GetAnyForInstanceAsync(instanceId).Returns((Session?)null);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        instance.Emit(new HarnessEvent
        {
            Type = "message.updated",
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow
        });
        instance.Complete();

        tracker.Register(instanceId, instance);

        // Cancel after a short time (aborting any retry waits)
        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);

        // No events should have been broadcast
        await broadcaster.DidNotReceive().BroadcastAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Test 4: Retry succeeds on a later attempt
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Session_lookup_retries_until_found()
    {
        var (broadcaster, sessionRepo, scopeFactory, broadcastSignal) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = new HarnessEventRelay(
            tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

        var instanceId = "instance-retry";
        var fleetSessionId = "fleet-retry-session";

        // Return null twice, then return the session
        var callCount = 0;
        sessionRepo.GetAnyForInstanceAsync(instanceId).Returns(_ =>
        {
            callCount++;
            return callCount >= 3
                ? Task.FromResult<Session?>(new Session { Id = fleetSessionId, InstanceId = instanceId })
                : Task.FromResult<Session?>(null);
        });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        // Brief delay to let the first session-lookup attempt complete (returns null),
        // then emit the event. The channel buffers it; the relay will drain it once
        // the third attempt succeeds and the pump starts iterating the instance stream.
        await Task.Delay(150); // first attempt returns null

        // Emit event after session is eventually found
        var evt = new HarnessEvent
        {
            Type = "session.status",
            SessionId = "oc-2",
            Timestamp = DateTimeOffset.UtcNow
        };
        instance.Emit(evt);
        instance.Complete();

        var result = await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal($"session:{fleetSessionId}", result.Topic);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Test 5: Already-running instances at startup are subscribed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Already_running_instances_at_startup_receive_relay()
    {
        var (broadcaster, sessionRepo, scopeFactory, broadcastSignal) = BuildDependencies();
        var tracker = new InstanceTracker();

        var instanceId = "instance-pre-existing";
        var fleetSessionId = "fleet-session-pre";

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        var instance = new FakeInstance(instanceId);

        // Register instance BEFORE relay starts
        tracker.Register(instanceId, instance);

        var relay = new HarnessEventRelay(
            tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        // Emit an event — relay should have subscribed to the pre-existing instance
        instance.Emit(new HarnessEvent
        {
            Type = "message.updated",
            SessionId = "oc-pre",
            Timestamp = DateTimeOffset.UtcNow
        });
        instance.Complete();

        var result = await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal($"session:{fleetSessionId}", result.Topic);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Test 6: Event payloads have session IDs rewritten to Fleet session ID
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Event_payload_sessionIds_are_rewritten_to_fleet_session_id()
    {
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var sessionRepo = Substitute.For<ISessionRepository>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        serviceProvider.GetService(typeof(ISessionRepository)).Returns(sessionRepo);
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        var fleetSessionId = "fleet-abc";
        var instanceId = "instance-payload-test";

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        // Capture the payload passed to broadcaster
        object? capturedPayload = null;
        broadcaster
            .When(b => b.BroadcastAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>()))
            .Do(call => capturedPayload = call.ArgAt<object>(2));

        var tracker = new InstanceTracker();
        var relay = new HarnessEventRelay(
            tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        // Emit event with OpenCode session IDs in the payload
        var openCodePayload = JsonSerializer.SerializeToElement(new
        {
            sessionID = "opencode-session-xyz",
            part = new
            {
                id = "part-1",
                sessionID = "opencode-session-xyz",
                messageID = "msg-1",
                type = "text",
                text = "Hello"
            }
        });

        instance.Emit(new HarnessEvent
        {
            Type = "message.part.updated",
            SessionId = "opencode-session-xyz",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = openCodePayload
        });
        instance.Complete();

        // Wait for broadcast
        await Task.Delay(500);

        Assert.NotNull(capturedPayload);

        // Verify the payload has Fleet session IDs
        var rewrittenJson = JsonSerializer.Serialize(capturedPayload);
        using var doc = JsonDocument.Parse(rewrittenJson);

        // Top-level sessionID should be Fleet ID
        Assert.Equal(fleetSessionId, doc.RootElement.GetProperty("sessionID").GetString());

        // Nested part.sessionID should also be Fleet ID
        Assert.Equal(fleetSessionId,
            doc.RootElement.GetProperty("part").GetProperty("sessionID").GetString());

        // Non-session-ID fields should be unchanged
        Assert.Equal("msg-1",
            doc.RootElement.GetProperty("part").GetProperty("messageID").GetString());

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Message persistence tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a scope factory backed by a real ServiceCollection so that
    /// GetRequiredService&lt;T&gt;() resolves correctly without NSubstitute IServiceProvider quirks.
    /// </summary>
    private static (IServiceScopeFactory ScopeFactory, ISessionRepository SessionRepo, IMessageRepository MessageRepo)
        BuildPersistenceDependencies()
    {
        var sessionRepo = Substitute.For<ISessionRepository>();
        var messageRepo = Substitute.For<IMessageRepository>();

        var services = new ServiceCollection();
        services.AddSingleton(sessionRepo);
        services.AddSingleton(messageRepo);
        var rootProvider = services.BuildServiceProvider();
        var scopeFactory = rootProvider.GetRequiredService<IServiceScopeFactory>();

        return (scopeFactory, sessionRepo, messageRepo);
    }

    /// <summary>
    /// Builds a valid OpenCode message.updated event payload with the given role.
    /// </summary>
    private static JsonElement BuildMessagePayload(string role = "assistant", string messageId = "msg-1") =>
        JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = messageId,
                sessionId = "oc-session",
                role,
                time = new { created = 1700000000L }
            },
            parts = new[] { new { type = "text", id = "p1", sessionId = "oc-session", messageId, text = "Hello" } }
        });

    [Fact]
    public async Task PumpAsync_MessageUpdatedEvent_DoesNotPersist()
    {
        var (scopeFactory, sessionRepo, messageRepo) = BuildPersistenceDependencies();
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var tracker = new InstanceTracker();

        var fleetSessionId = "fleet-persist-1";
        var instanceId = "inst-persist-1";
        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        // Use a broadcast signal to know when the event has been fully processed
        var broadcastSignal = new TaskCompletionSource<string>();
        broadcaster
            .When(b => b.BroadcastAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>()))
            .Do(call => broadcastSignal.TrySetResult(call.ArgAt<string>(1)));

        var relay = new HarnessEventRelay(tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);
        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = "message.updated",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = BuildMessagePayload("assistant")
        });
        instance.Complete();

        // Wait for the broadcast to confirm the event was processed through the pump
        var broadcastedType = await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("message.updated", broadcastedType);

        // Give fire-and-forget tasks time to complete
        await Task.Delay(200);

        // message.updated must NOT trigger UpsertAsync
        await messageRepo.DidNotReceive().UpsertAsync(Arg.Any<PersistedMessage>());

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PumpAsync_MessageCreatedEvent_PersistsMessage()
    {
        var (scopeFactory, sessionRepo, messageRepo) = BuildPersistenceDependencies();
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var tracker = new InstanceTracker();

        var fleetSessionId = "fleet-persist-2";
        var instanceId = "inst-persist-2";
        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        // Use a TCS-backed Returns so the Do side-effect fires reliably for async methods
        var persistSignal = new TaskCompletionSource();
        messageRepo.UpsertAsync(Arg.Any<PersistedMessage>())
            .Returns(callInfo =>
            {
                persistSignal.TrySetResult();
                return Task.CompletedTask;
            });

        var relay = new HarnessEventRelay(tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);
        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = "message.created",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = BuildMessagePayload("user", "msg-user-1")
        });
        instance.Complete();

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await messageRepo.Received(1).UpsertAsync(Arg.Is<PersistedMessage>(m =>
            m.SessionId == fleetSessionId && m.Role == "user"));

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PumpAsync_NonMessageEvent_DoesNotPersist()
    {
        var (scopeFactory, sessionRepo, messageRepo) = BuildPersistenceDependencies();
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var tracker = new InstanceTracker();

        var instanceId = "inst-persist-3";
        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = "fleet-persist-3", InstanceId = instanceId });

        // Use a broadcast signal to know when the event has been fully processed
        var broadcastSignal = new TaskCompletionSource();
        broadcaster
            .When(b => b.BroadcastAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>()))
            .Do(_ => broadcastSignal.TrySetResult());

        var relay = new HarnessEventRelay(tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);
        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = "session.status",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = "idle" })
        });
        instance.Complete();

        // Wait for the broadcast to confirm the event was processed through the pump
        await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await messageRepo.DidNotReceive().UpsertAsync(Arg.Any<PersistedMessage>());

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PumpAsync_PersistenceFailure_DoesNotBlockEventDelivery()
    {
        var (scopeFactory, sessionRepo, messageRepo) = BuildPersistenceDependencies();
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var tracker = new InstanceTracker();

        var fleetSessionId = "fleet-persist-4";
        var instanceId = "inst-persist-4";
        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        // Repository always throws
        messageRepo.UpsertAsync(Arg.Any<PersistedMessage>())
            .Returns(_ => Task.FromException(new InvalidOperationException("DB is on fire")));

        var broadcastSignal = new TaskCompletionSource<string>();
        broadcaster
            .When(b => b.BroadcastAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>()))
            .Do(call => broadcastSignal.TrySetResult(call.ArgAt<string>(1)));

        var relay = new HarnessEventRelay(tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);
        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = "message.created",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = BuildMessagePayload("assistant")
        });
        instance.Complete();

        // Broadcast must succeed despite DB failure
        var broadcastedType = await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("message.created", broadcastedType);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Test double: controllable harness instance
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a valid message.part.updated event payload with a text part.
    /// </summary>
    private static JsonElement BuildPartPayload(
        string messageId = "msg-1",
        string sessionId = "oc-session",
        string text = "Hello from part") =>
        JsonSerializer.SerializeToElement(new
        {
            part = new
            {
                id = "part-1",
                sessionID = sessionId,
                messageID = messageId,
                type = "text",
                text
            }
        });

    [Fact]
    public async Task PumpAsync_MessagePartUpdated_TextPart_PersistsIncrementally()
    {
        var (scopeFactory, sessionRepo, messageRepo) = BuildPersistenceDependencies();
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var tracker = new InstanceTracker();

        var fleetSessionId = "fleet-part-persist-1";
        var instanceId = "inst-part-persist-1";
        var messageId = "msg-part-1";

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        // No existing message in DB
        messageRepo.GetByIdAsync(messageId, fleetSessionId)
            .Returns(Task.FromResult<PersistedMessage?>(null));

        var persistSignal = new TaskCompletionSource();
        messageRepo.UpsertAsync(Arg.Any<PersistedMessage>())
            .Returns(callInfo =>
            {
                persistSignal.TrySetResult();
                return Task.CompletedTask;
            });

        var relay = new HarnessEventRelay(tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);
        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = "message.part.updated",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = BuildPartPayload(messageId, "oc-session", "Hello from part")
        });
        instance.Complete();

        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await messageRepo.Received(1).UpsertAsync(Arg.Is<PersistedMessage>(m =>
            m.Id == messageId &&
            m.SessionId == fleetSessionId &&
            m.Role == "assistant" &&
            m.PartsJson.Contains("Hello from part")));

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PumpAsync_MessageCreated_EmptyParts_DoesNotOverwriteExisting()
    {
        var (scopeFactory, sessionRepo, messageRepo) = BuildPersistenceDependencies();
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var tracker = new InstanceTracker();

        var fleetSessionId = "fleet-guard-1";
        var instanceId = "inst-guard-1";
        var messageId = "msg-guard-1";

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        // Existing message with non-empty parts
        var existingMessage = new PersistedMessage
        {
            Id = messageId,
            SessionId = fleetSessionId,
            Role = "assistant",
            PartsJson = """[{"type":"text","text":"existing content"}]""",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        messageRepo.GetByIdAsync(messageId, fleetSessionId)
            .Returns(Task.FromResult<PersistedMessage?>(existingMessage));

        // Use broadcast signal to know the event was processed
        var broadcastSignal = new TaskCompletionSource();
        broadcaster
            .When(b => b.BroadcastAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object>(), Arg.Any<CancellationToken>()))
            .Do(_ => broadcastSignal.TrySetResult());

        var relay = new HarnessEventRelay(tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);
        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        // Emit message.created with empty parts (assistant skeleton)
        var emptyPartsPayload = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = messageId,
                sessionId = "oc-session",
                role = "assistant",
                time = new { created = 1700000000L }
            },
            parts = Array.Empty<object>()
        });

        instance.Emit(new HarnessEvent
        {
            Type = "message.created",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = emptyPartsPayload
        });
        instance.Complete();

        // Wait for broadcast and fire-and-forget to settle
        await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        // UpsertAsync must NOT be called — guard prevented overwrite
        await messageRepo.DidNotReceive().UpsertAsync(Arg.Any<PersistedMessage>());

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PumpAsync_FullLifecycle_MessageUpdatedDoesNotOverwriteParts()
    {
        var (scopeFactory, sessionRepo, messageRepo) = BuildPersistenceDependencies();
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var tracker = new InstanceTracker();

        var fleetSessionId = "fleet-lifecycle-1";
        var instanceId = "inst-lifecycle-1";
        var messageId = "msg-lifecycle-1";

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        // Track what was upserted via a sequence: created→part.updated→updated
        PersistedMessage? lastUpserted = null;
        var persistedMessages = new List<PersistedMessage>();

        // Step 1: message.created with empty parts → GetByIdAsync returns null (first call)
        // Step 2: message.part.updated → GetByIdAsync returns the skeleton
        // Track call counts to simulate state progression
        var getByIdCallCount = 0;
        messageRepo.GetByIdAsync(messageId, fleetSessionId)
            .Returns(callInfo =>
            {
                getByIdCallCount++;
                // First call (from message.created guard): no existing message
                // Subsequent calls (from TryPersistPartAsync): return last upserted
                return Task.FromResult(lastUpserted);
            });

        var upsertSignal = new TaskCompletionSource();
        messageRepo.UpsertAsync(Arg.Any<PersistedMessage>())
            .Returns(callInfo =>
            {
                lastUpserted = callInfo.ArgAt<PersistedMessage>(0);
                persistedMessages.Add(lastUpserted);
                upsertSignal.TrySetResult();
                return Task.CompletedTask;
            });

        var relay = new HarnessEventRelay(tracker, broadcaster, scopeFactory, NullLogger<HarnessEventRelay>.Instance);
        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
        tracker.Register(instanceId, instance);

        // 1) message.created (empty assistant skeleton)
        var skeletonPayload = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = messageId,
                sessionId = "oc-session",
                role = "assistant",
                time = new { created = 1700000000L }
            },
            parts = Array.Empty<object>()
        });
        instance.Emit(new HarnessEvent
        {
            Type = "message.created",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = skeletonPayload
        });

        // 2) message.part.updated (text part arrives)
        instance.Emit(new HarnessEvent
        {
            Type = "message.part.updated",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = BuildPartPayload(messageId, "oc-session", "The actual answer")
        });

        // 3) message.updated (metadata only — no parts)
        instance.Emit(new HarnessEvent
        {
            Type = "message.updated",
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = BuildMessagePayload("assistant", messageId)
        });

        instance.Complete();

        // Wait for at least one upsert (from part.updated)
        await upsertSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300); // Let all fire-and-forget tasks settle

        // Final state must contain the text part (not overwritten by message.updated)
        Assert.NotNull(lastUpserted);
        Assert.Contains("The actual answer", lastUpserted.PartsJson);

        // The final persisted state must not be empty
        Assert.NotEqual("[]", lastUpserted.PartsJson);

        // At least one upsert must have occurred (from message.part.updated)
        Assert.True(persistedMessages.Count >= 1, "Expected at least one UpsertAsync call from part.updated");

        // message.updated must NOT have been the last event to write — final state has parts
        // (message.updated is filtered out entirely, so it never calls UpsertAsync)

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    private sealed class FakeInstance : IHarnessInstance
    {
        private readonly System.Threading.Channels.Channel<HarnessEvent> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<HarnessEvent>();

        public FakeInstance(string instanceId) { InstanceId = instanceId; }

        public string InstanceId { get; }
        public string HarnessType => "fake";
        public HarnessInstanceStatus Status => HarnessInstanceStatus.Running;

        public void Emit(HarnessEvent evt) => _channel.Writer.TryWrite(evt);
        public void Complete() => _channel.Writer.Complete();

        public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
                yield return evt;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct) =>
            Task.CompletedTask;
        public Task AbortAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct) =>
            Task.FromResult(new MessagePage([], false));
        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct) =>
            Task.FromResult(new HealthCheckResult(true, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
