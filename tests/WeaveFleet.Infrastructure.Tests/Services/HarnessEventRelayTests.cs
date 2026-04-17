using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

public sealed class HarnessEventRelayTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static (
        FakeEventBroadcaster Broadcaster,
        InMemorySessionRepository SessionRepo,
        IServiceScopeFactory ScopeFactory,
        SessionActivityTracker ActivityTracker,
        TaskCompletionSource<(string Topic, string Type)> BroadcastSignal
    ) BuildDependencies()
    {
        var broadcaster = new FakeEventBroadcaster();
        var sessionRepo = new InMemorySessionRepository();
        var activityTracker = new SessionActivityTracker();
        var messageRepo = new InMemoryMessageRepository();
        var delegationRepo = new InMemoryDelegationRepository();
        var outboxRepo = new InMemoryOutboxRepository();
        var outboxDispatcher = new FakeOutboxDispatcher();
        var connectionFactory = new FakeDbConnectionFactory();

        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<ISessionRepository>((ISessionRepository)sessionRepo);
            services.AddSingleton<IMessageRepository>((IMessageRepository)messageRepo);
            services.AddSingleton<IDelegationRepository>((IDelegationRepository)delegationRepo);
            services.AddSingleton<IOutboxRepository>((IOutboxRepository)outboxRepo);
            services.AddSingleton<IOutboxDispatcher>((IOutboxDispatcher)outboxDispatcher);
            services.AddSingleton<IDbConnectionFactory>((IDbConnectionFactory)connectionFactory);
            services.AddSingleton(new SessionActivityWriteService(
                connectionFactory,
                messageRepo,
                delegationRepo,
                sessionRepo,
                outboxRepo,
                outboxDispatcher));
        });

        var broadcastSignal = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster.OnBroadcast = (topic, type, _, _, _) =>
            broadcastSignal.TrySetResult((topic, type));

        return (broadcaster, sessionRepo, scopeFactory, activityTracker, broadcastSignal);
    }

    private static HarnessEventRelay BuildRelay(
        InstanceTracker tracker,
        FakeEventBroadcaster broadcaster,
        SessionActivityTracker activityTracker,
        IServiceScopeFactory scopeFactory)
        => new(tracker, broadcaster, new FakeEventPublisher(), activityTracker, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

    private static HarnessEventRelay BuildRelay(
        InstanceTracker tracker,
        FakeEventBroadcaster broadcaster,
        FakeEventPublisher publisher,
        SessionActivityTracker activityTracker,
        IServiceScopeFactory scopeFactory)
        => new(tracker, broadcaster, publisher, activityTracker, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

    // -----------------------------------------------------------------------
    // Test 1: Happy path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Ephemeral_events_are_broadcast_on_fleet_session_topic()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, broadcastSignal) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-session-123";
        var instanceId = "instance-abc";

        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);

        // Give ExecuteAsync time to wire event handlers
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        // Emit an event and complete the stream
        var evt = new HarnessEvent
        {
            Type = "session.status",
            SessionId = "oc-session-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" } })
        };
        instance.Emit(evt);
        instance.Complete();

        // Wait for broadcast
        var result = await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        result.Topic.ShouldBe($"session:{fleetSessionId}");
        result.Type.ShouldBe("session.status");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Test 2: Instance removed cancels subscription
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Removing_instance_cancels_its_subscription()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var instanceId = "instance-xyz";
        sessionRepo.Seed(new Session { Id = "fleet-session-xyz", InstanceId = instanceId });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        // Instance that blocks indefinitely (no events, no completion)
        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        // Let pump start
        await Task.Delay(100);

        // Remove the instance — should cancel its subscription
        tracker.Remove(instanceId);

        // Instance's SubscribeAsync should be cancelled via the pump's CT.
        // Wait briefly then verify no activity_status events were broadcast on the session topic
        await Task.Delay(200);

        // Only the idle broadcast from the finally block should have been called (on "sessions" topic)
        broadcaster.Broadcasts.ShouldNotContain(b => b.Topic.StartsWith("session:"));

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Test 3: Session lookup not found — no events are broadcast
    // -----------------------------------------------------------------------

    [Fact]
    public async Task No_events_broadcast_when_session_lookup_always_fails()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var instanceId = "instance-no-session";

        // Session repo always returns null (nothing seeded for this instanceId)
        sessionRepo.GetAnyForInstanceBehavior = _ => Task.FromResult<Session?>(null);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        instance.Emit(new HarnessEvent
        {
            Type = "session.status",
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow
        });
        instance.Complete();

        tracker.Register(instanceId, instance);

        // Cancel after a short time (aborting any retry waits)
        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);

        // No events should have been broadcast
        broadcaster.Broadcasts.ShouldBeEmpty();
    }

    // -----------------------------------------------------------------------
    // Test 4: Retry succeeds on a later attempt
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Session_lookup_retries_until_found()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, broadcastSignal) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var instanceId = "instance-retry";
        var fleetSessionId = "fleet-retry-session";

        // Return null twice, then return the session
        var callCount = 0;
        sessionRepo.GetAnyForInstanceBehavior = _ =>
        {
            callCount++;
            return callCount >= 3
                ? Task.FromResult<Session?>(new Session { Id = fleetSessionId, InstanceId = instanceId })
                : Task.FromResult<Session?>(null);
        };

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
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

        result.Topic.ShouldBe($"session:{fleetSessionId}");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Test 5: Already-running instances at startup are subscribed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Already_running_instances_at_startup_receive_relay()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, broadcastSignal) = BuildDependencies();
        var tracker = new InstanceTracker();

        var instanceId = "instance-pre-existing";
        var fleetSessionId = "fleet-session-pre";

        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId });

        var instance = new FakeHarnessSession(instanceId);

        // Register instance BEFORE relay starts
        tracker.Register(instanceId, instance);

        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        // Emit an event — relay should have subscribed to the pre-existing instance
        instance.Emit(new HarnessEvent
        {
            Type = "message.part.delta",
            SessionId = "oc-pre",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { messageID = "m1", partID = "p1", field = "text", delta = "hi" })
        });
        instance.Complete();

        var result = await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        result.Topic.ShouldBe($"session:{fleetSessionId}");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ephemeral_event_payload_sessionIds_are_preserved_when_broadcast()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();

        var fleetSessionId = "fleet-abc";
        var instanceId = "instance-payload-test";

        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId });

        object? capturedPayload = null;
        var payloadSignal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster.OnBroadcast = (topic, _, payload, _, _) =>
        {
            // Capture the first broadcast on the per-session topic (not the "sessions" topic)
            if (topic.StartsWith("session:", StringComparison.Ordinal))
            {
                capturedPayload = payload;
                payloadSignal.TrySetResult(capturedPayload);
            }
        };

        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        var payload = JsonSerializer.SerializeToElement(new
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
            Type = "message.part.delta",
            SessionId = "opencode-session-xyz",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload
        });
        instance.Complete();

        capturedPayload = await payloadSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        capturedPayload.ShouldNotBeNull();

        var broadcastJson = JsonSerializer.Serialize(capturedPayload);
        using var doc = JsonDocument.Parse(broadcastJson);

        doc.RootElement.GetProperty("sessionID").GetString().ShouldBe("opencode-session-xyz");
        doc.RootElement.GetProperty("part").GetProperty("sessionID").GetString().ShouldBe("opencode-session-xyz");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Durable_events_are_not_relayed()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-durable-skip";
        var instanceId = "instance-durable-skip";

        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = "message.updated",
            SessionId = "oc-durable",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { id = "msg-1" } })
        });
        instance.Complete();

        await Task.Delay(200);

        broadcaster.Broadcasts.ShouldNotContain(b => b.Topic.StartsWith("session:"));

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Child_routed_events_are_broadcast_on_child_topic()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, broadcastSignal) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-parent";
        var instanceId = "instance-child-topic";

        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = "message.part.delta",
            SessionId = "oc-child",
            FleetSessionId = "fleet-child",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { messageID = "msg-1", partID = "part-1", field = "text", delta = "child" })
        });
        instance.Complete();

        var result = await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        result.Topic.ShouldBe("session:fleet-child");
        result.Type.ShouldBe("message.part.delta");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // New tests: activity tracking and global broadcast
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SessionStatusBusyEventUpdatesTrackerAndBroadcastsOnSessionsTopic()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();

        var fleetSessionId = "fleet-activity-busy";
        var instanceId = "instance-activity-busy";

        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "user-1" });

        // Capture the first "busy" activity_status broadcast on "sessions" topic
        var busySignal = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster.OnBroadcast = (topic, type, payload, _, _) =>
        {
            if (topic == "sessions" && type == "activity_status")
            {
                var json = JsonSerializer.Serialize(payload);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("activityStatus", out var statusProp)
                    && statusProp.GetString() == "busy")
                {
                    busySignal.TrySetResult(payload);
                }
            }
        };

        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = "session.status",
            SessionId = "oc-busy",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" } })
        });

        // Wait for the busy broadcast before completing the stream
        var payload = await busySignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("sessionId").GetString().ShouldBe(fleetSessionId);
        doc.RootElement.GetProperty("activityStatus").GetString().ShouldBe("busy");

        // Tracker should also be updated (check before completing stream to avoid finally cleanup)
        var snapshot = activityTracker.Get(fleetSessionId);
        snapshot.ShouldNotBeNull();
        snapshot.ActivityStatus.ShouldBe("busy");

        instance.Complete();
        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SessionIdleEventUpdatesTrackerAndBroadcastsOnSessionsTopic()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();

        var fleetSessionId = "fleet-activity-idle";
        var instanceId = "instance-activity-idle";

        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "user-1" });

        // Capture the first "idle" activity_status broadcast from the event (not the finally block)
        // We use a counter to distinguish: first idle = from event, second idle = from finally
        var idleFromEventSignal = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleCount = 0;
        broadcaster.OnBroadcast = (topic, type, payload, _, _) =>
        {
            if (topic == "sessions" && type == "activity_status")
            {
                var json = JsonSerializer.Serialize(payload);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("activityStatus", out var statusProp)
                    && statusProp.GetString() == "idle")
                {
                    if (Interlocked.Increment(ref idleCount) == 1)
                        idleFromEventSignal.TrySetResult(payload);
                }
            }
        };

        using var cts = new CancellationTokenSource();
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = "session.idle",
            SessionId = "oc-idle",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { })
        });

        // Wait for the idle broadcast from the event before completing the stream
        var payload = await idleFromEventSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("sessionId").GetString().ShouldBe(fleetSessionId);
        doc.RootElement.GetProperty("activityStatus").GetString().ShouldBe("idle");

        // Tracker should also be updated
        var snapshot = activityTracker.Get(fleetSessionId);
        snapshot.ShouldNotBeNull();
        snapshot.ActivityStatus.ShouldBe("idle");

        instance.Complete();
        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InstanceRemovalClearsTrackerAndBroadcastsIdle()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();

        var fleetSessionId = "fleet-disconnect";
        var instanceId = "instance-disconnect";

        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "user-1" });

        // Pre-populate tracker with busy state
        activityTracker.Update(fleetSessionId, "busy", "user-1");

        // Capture broadcasts on "sessions" topic
        var idleSignal = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster.OnBroadcast = (topic, type, payload, _, _) =>
        {
            if (topic == "sessions" && type == "activity_status")
            {
                var json = JsonSerializer.Serialize(payload);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("activityStatus", out var statusProp)
                    && statusProp.GetString() == "idle")
                {
                    idleSignal.TrySetResult(payload);
                }
            }
        };

        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);
        await Task.Delay(100);

        // Complete the stream (simulates harness disconnect)
        instance.Complete();

        // Wait for the idle broadcast from the finally block
        await idleSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Tracker should be cleared
        activityTracker.Get(fleetSessionId).ShouldBeNull();

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Durable_events_are_persisted_even_when_no_frontend_subscriber_exists()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-durable-persist";
        var instanceId = "instance-durable-persist";
        var messageId = "msg-durable-persist-1";

        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "user-1" });

        // Retrieve the message repo from the scope factory to inspect upsert calls
        using var inspectScope = scopeFactory.CreateScope();
        var messageRepo = (InMemoryMessageRepository)inspectScope.ServiceProvider.GetRequiredService<IMessageRepository>();

        var persistSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messageRepo.UpsertBehavior = _ =>
        {
            persistSignal.TrySetResult();
            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        // Emit a durable event (message.updated) — no frontend subscriber
        instance.Emit(new HarnessEvent
        {
            Type = "message.updated",
            SessionId = "oc-durable-persist",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = messageId,
                    sessionId = "oc-durable-persist",
                    role = "assistant",
                    time = new { created = 1700000000L }
                },
                parts = new[] { new { type = "text", id = "p1", sessionId = "oc-durable-persist", messageId, text = "Persisted without subscriber" } }
            })
        });
        instance.Complete();

        // Wait for persistence to fire
        await persistSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        messageRepo.UpsertCalls.ShouldContain(m =>
            m.Id == messageId &&
            m.SessionId == fleetSessionId &&
            m.PartsJson.Contains("Persisted without subscriber"));

        // Durable event must NOT be broadcast to frontend
        broadcaster.Broadcasts.ShouldNotContain(b => b.Topic == $"session:{fleetSessionId}" && b.Type == "message.updated");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Dual-write: every event should also flow through IEventPublisher
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Relay_dualWrites_everyEventToEventPublisher_withMonotonicSequence()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-dual-1";
        var instanceId = "instance-dual-1";
        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-xyz",
            ProjectId = "proj-dual",
            HarnessType = "opencode",
        });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        // Emit multiple events
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" } })
        });
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessagePartDelta,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { delta = "x" })
        });
        instance.Complete();

        // Poll until the publisher has seen both events
        for (int i = 0; i < 50 && publisher.Calls.Count < 2; i++)
        {
            await Task.Delay(50);
        }

        publisher.Calls.Count.ShouldBeGreaterThanOrEqualTo(2);

        // Per-pump monotonic sequence: every Nats-Msg-Id seq is strictly increasing.
        var seqs = publisher.Calls.Select(c => c.Context.Sequence).ToArray();
        for (int i = 1; i < seqs.Length; i++)
            seqs[i].ShouldBeGreaterThan(seqs[i - 1]);

        // Project/user/harness-type pass through from the session row.
        var first = publisher.Calls.First();
        first.Context.FleetSessionId.ShouldBe(fleetSessionId);
        first.Context.ProjectId.ShouldBe("proj-dual");
        first.Context.UserId.ShouldBe("user-xyz");
        first.Context.HarnessType.ShouldBe("opencode");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Relay_publishFailure_doesNotBreakLegacyPath()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, broadcastSignal) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher
        {
            // Force every publish to throw — simulating a broken broker.
            ShouldFail = true,
        };
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-pub-fail";
        var instanceId = "instance-pub-fail";
        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "u" });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" } })
        });
        instance.Complete();

        // Legacy broadcaster path still fires — publish failure is swallowed.
        var result = await broadcastSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Topic.ShouldBe($"session:{fleetSessionId}");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }
}
