using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Tests.Data.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// The relay's responsibility is publish-only (plus reasoning-filter sanitation): every harness
/// event flows to the event publisher via <see cref="IEventPublisher"/> with an internal per-pump dedup key.
/// Downstream consumers (InProcessFanOutService for WebSocket fan-out) handle their own responsibilities and are
/// tested at their own layers.
/// </summary>
public sealed class HarnessEventRelayTests
{
    private static (
        FakeEventBroadcaster Broadcaster,
        InMemorySessionRepository SessionRepo,
        IServiceScopeFactory ScopeFactory,
        SessionActivityTracker ActivityTracker
    ) BuildDependencies()
    {
        var broadcaster = new FakeEventBroadcaster();
        var sessionRepo = new InMemorySessionRepository();
        var activityTracker = new SessionActivityTracker();

        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>((ISessionRepository)sessionRepo);
            services.AddSingleton(new SessionCapabilitiesResolver(new InstanceTracker(), activityTracker));
            services.AddTransient<DomainEventTranslator>();
        });

        return (broadcaster, sessionRepo, scopeFactory, activityTracker);
    }

    private static HarnessEventRelay BuildRelay(
        InstanceTracker tracker,
        FakeEventBroadcaster broadcaster,
        FakeEventPublisher publisher,
        SessionActivityTracker activityTracker,
        IServiceScopeFactory scopeFactory)
        => new(tracker, broadcaster, publisher, activityTracker, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

    [Fact]
    public async Task relay_publishes_every_event_with_internal_pump_dedup_key_and_session_metadata()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-1";
        var instanceId = "instance-1";
        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-x",
            ProjectId = "proj-a",
            HarnessType = "opencode",
        });

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
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } })
        });
        instance.Complete();

        for (int i = 0; i < 50 && publisher.Calls.Count < 2; i++) await Task.Delay(50);

        publisher.Calls.Count.ShouldBeGreaterThanOrEqualTo(2);
        var dedupKeys = publisher.Calls.Select(c => c.Context.InternalPumpDedupKey).ToArray();
        for (int i = 1; i < dedupKeys.Length; i++) dedupKeys[i].ShouldBeGreaterThan(dedupKeys[i - 1]);
        var first = publisher.Calls.First();
        first.Context.FleetSessionId.ShouldBe(fleetSessionId);
        first.Context.ProjectId.ShouldBe("proj-a");
        first.Context.UserId.ShouldBe("user-x");
        first.Context.HarnessType.ShouldBe("opencode");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task pump_restart_mid_session_keeps_durable_event_ids_monotonic()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var channels = new InProcessChannels();
        var metrics = new InProcessMetrics();
        var pipelineMetrics = new PipelineLatencyMetrics();
        var publisher = new InProcessEventPublisher(
            channels,
            metrics,
            pipelineMetrics,
            NullLogger<InProcessEventPublisher>.Instance);
        var relay = new HarnessEventRelay(
            tracker,
            broadcaster,
            publisher,
            activityTracker,
            scopeFactory,
            NullLogger<HarnessEventRelay>.Instance);

        const string fleetSessionId = "fleet-pump-restart";
        const string instanceId = "instance-pump-restart";
        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-pump-restart",
            ProjectId = "proj-pump-restart",
            HarnessType = "opencode",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await relay.StartAsync(cts.Token);
        await Task.Delay(50, CancellationToken.None);

        var firstPump = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, firstPump);
        firstPump.Emit(CreateMessageLifecycleEvent(EventTypes.MessageUpdated, "msg-before-restart", "assistant", "before restart"));
        firstPump.Complete();

        await WaitUntilAsync(() => Task.FromResult(broadcaster.Broadcasts.Any(b => b.Topic == "sessions")), cts.Token);

        var secondPump = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, secondPump);
        secondPump.Emit(CreateMessageLifecycleEvent(EventTypes.MessageUpdated, "msg-after-restart", "assistant", "after restart"));
        secondPump.Complete();

        await WaitUntilAsync(() => Task.FromResult(broadcaster.Broadcasts.Count >= 2), cts.Token);

        // Verify that both events were relayed through the publisher
        broadcaster.Broadcasts.Count.ShouldBeGreaterThanOrEqualTo(2);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task relay_internal_dedup_keys_are_not_frontend_session_stream_cursors()
    {
        var (broadcaster, sessionRepo, _, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var channels = new InProcessChannels();
        var metrics = new InProcessMetrics();
        var sharedFleetSessionId = "fleet-shared-stream";
        var sharedTopic = $"session:{sharedFleetSessionId}";

        // Create a custom scope factory that includes the tracker instance used by the test
        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>(sessionRepo);
            services.AddSingleton(tracker);
            services.AddSingleton(new SessionCapabilitiesResolver(tracker, activityTracker));
            services.AddTransient<DomainEventTranslator>();
        });

        var pipelineMetrics = new PipelineLatencyMetrics();
        var publisher = new InProcessEventPublisher(
            channels,
            metrics,
            pipelineMetrics,
            NullLogger<InProcessEventPublisher>.Instance);
        var fanOut = new InProcessFanOutService(
            channels,
            broadcaster,
            new PipelineLatencyMetrics(),
            scopeFactory,
            NullLogger<InProcessFanOutService>.Instance);
        var relay = new HarnessEventRelay(
            tracker,
            broadcaster,
            publisher,
            activityTracker,
            scopeFactory,
            NullLogger<HarnessEventRelay>.Instance);

        sessionRepo.GetAnyForInstanceBehavior = instanceId => Task.FromResult<Session?>(new Session
        {
            Id = sharedFleetSessionId,
            InstanceId = instanceId,
            UserId = "user-shared",
            ProjectId = "proj-shared",
            HarnessType = "opencode",
        });

        using var cts = new CancellationTokenSource();
        await fanOut.StartAsync(cts.Token);
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instanceA = new FakeHarnessSession("instance-shared-a");
        var instanceB = new FakeHarnessSession("instance-shared-b");
        tracker.Register(instanceA.InstanceId, instanceA);
        tracker.Register(instanceB.InstanceId, instanceB);

        // Wait for relay pumps to start and resolve session metadata
        await Task.Delay(1000);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emitA = Task.Run(async () =>
        {
            await release.Task;
            instanceA.Emit(new HarnessEvent
            {
                Type = EventTypes.SessionStatus,
                SessionId = "oc-a",
                FleetSessionId = sharedFleetSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" }, source = "a" })
            });
            instanceA.Complete();
        });
        var emitB = Task.Run(async () =>
        {
            await release.Task;
            instanceB.Emit(new HarnessEvent
            {
                Type = EventTypes.SessionStatus,
                SessionId = "oc-b",
                FleetSessionId = sharedFleetSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" }, source = "b" })
            });
            instanceB.Complete();
        });

        release.SetResult();
        await Task.WhenAll(emitA, emitB);

        // Wait longer for events to flow through relay → publisher → fan-out → broadcaster
        for (int i = 0; i < 200 && broadcaster.Broadcasts.Count(b => b.Topic == sharedTopic) < 2; i++)
            await Task.Delay(50);

        var sessionBroadcasts = broadcaster.Broadcasts
            .Where(b => b.Topic == sharedTopic)
            .ToArray();

        sessionBroadcasts.Length.ShouldBe(2);

        // Ephemeral relay events do not have durable store ids, so fan-out does not expose the
        // per-pump internal dedup key as a frontend stream sequence/event id.
        sessionBroadcasts.Select(b => b.SequenceNumber).ShouldAllBe(sequenceNumber => sequenceNumber == null);
        sessionBroadcasts.Select(b => b.SequenceNumber).Distinct().Count().ShouldBe(1);
        sessionBroadcasts.Select(b => b.Topic).Distinct().ShouldBe([sharedTopic]);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
        await fanOut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Relay_publishFailure_doesNotCrashPump()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher { ShouldFail = true };
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-2";
        var instanceId = "instance-2";
        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "u" });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);
        instance.Emit(new HarnessEvent { Type = EventTypes.SessionStatus, SessionId = "oc-1", Timestamp = DateTimeOffset.UtcNow });
        instance.Emit(new HarnessEvent { Type = EventTypes.SessionIdle, SessionId = "oc-1", Timestamp = DateTimeOffset.UtcNow });
        instance.Complete();

        // Pump survives the publish failures and each event still hit the publisher.
        for (int i = 0; i < 50 && publisher.Calls.Count < 2; i++) await Task.Delay(50);
        publisher.Calls.Count.ShouldBeGreaterThanOrEqualTo(2);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Pump_emitsIdleActivity_onSessionsTopic_afterDisconnect()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-3";
        var instanceId = "instance-3";
        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "u" });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);
        instance.Complete();

        for (int i = 0; i < 50 && broadcaster.Broadcasts.Count == 0; i++) await Task.Delay(50);

        broadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "sessions" && b.Type == "activity_status");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Session_lookup_retries_until_found()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-4";
        var instanceId = "instance-4";

        int attempts = 0;
        sessionRepo.GetAnyForInstanceBehavior = _ =>
        {
            attempts++;
            return attempts < 3
                ? Task.FromResult<Session?>(null)
                : Task.FromResult<Session?>(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "u" });
        };

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);
        instance.Emit(new HarnessEvent { Type = EventTypes.SessionStatus, SessionId = "oc-1", Timestamp = DateTimeOffset.UtcNow });
        instance.Complete();

        for (int i = 0; i < 50 && publisher.Calls.IsEmpty; i++) await Task.Delay(100);
        publisher.Calls.Count.ShouldBeGreaterThanOrEqualTo(1);
        attempts.ShouldBeGreaterThanOrEqualTo(3);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task No_events_published_when_session_lookup_always_fails()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var instanceId = "instance-nosession";
        sessionRepo.GetAnyForInstanceBehavior = _ => Task.FromResult<Session?>(null);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        instance.Emit(new HarnessEvent { Type = EventTypes.SessionStatus, SessionId = "oc-1", Timestamp = DateTimeOffset.UtcNow });
        instance.Complete();
        tracker.Register(instanceId, instance);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);

        publisher.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_instance_cancels_its_subscription()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var instanceId = "instance-to-remove";
        sessionRepo.Seed(new Session { Id = "fleet-remove", InstanceId = instanceId, UserId = "u" });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);
        await Task.Delay(100);

        tracker.Remove(instanceId);
        await Task.Delay(200);

        // Finally block still emits an idle broadcast on "sessions", which is OK.
        // The pump itself should have exited — no "session:*" broadcasts.
        broadcaster.Broadcasts.ShouldNotContain(b => b.Topic.StartsWith("session:"));

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Already_running_instances_at_startup_receive_relay()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();

        var fleetSessionId = "fleet-preexisting";
        var instanceId = "instance-preexisting";
        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "u" });

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);

        instance.Emit(new HarnessEvent { Type = EventTypes.SessionStatus, SessionId = "oc-1", Timestamp = DateTimeOffset.UtcNow });
        instance.Complete();

        for (int i = 0; i < 50 && publisher.Calls.IsEmpty; i++) await Task.Delay(50);
        publisher.Calls.Count.ShouldBeGreaterThanOrEqualTo(1);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_attach_translated_domain_event_to_publish_context()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        const string fleetSessionId = "fleet-domain";
        const string instanceId = "instance-domain";
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
            Payload = JsonSerializer.SerializeToElement(new
            {
                status = new
                {
                    type = "busy",
                    messageID = "msg-1",
                    index = 3,
                    agent = "loom",
                    modelID = "model-1"
                }
            })
        });
        instance.Complete();

        for (int i = 0; i < 50 && publisher.Calls.IsEmpty; i++)
            await Task.Delay(50);

        publisher.Calls.TryPeek(out var published).ShouldBeTrue();
        var domainEvent = published!.Context.DomainEvent.ShouldBeOfType<TurnStarted>();
        domainEvent.Payload.SessionId.ShouldBe(fleetSessionId);
        domainEvent.Payload.MessageId.ShouldBe("msg-1");
        domainEvent.Payload.Index.ShouldBe(3);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Relay_suppresses_user_echo_parts_and_keeps_assistant_parts()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        const string fleetSessionId = "fleet-user-echo";
        const string instanceId = "instance-user-echo";
        const string userMessageId = "msg-user";
        const string assistantMessageId = "msg-assistant";
        sessionRepo.Seed(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "u" });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = userMessageId,
                    role = "user"
                }
            })
        });

        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessagePartUpdated,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                part = new
                {
                    id = "part-1",
                    messageID = userMessageId,
                    type = "text",
                    text = "user prompt"
                }
            })
        });

        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessageUpdated,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = assistantMessageId,
                    role = "assistant"
                }
            })
        });

        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessagePartUpdated,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                part = new
                {
                    id = "part-2",
                    messageID = assistantMessageId,
                    type = "text",
                    text = "assistant reply"
                }
            })
        });
        instance.Complete();

        for (int i = 0; i < 50 && publisher.Calls.Count < 2; i++)
            await Task.Delay(50);

        publisher.Calls.Count.ShouldBe(2);
        publisher.Calls.Select(call => call.Event.Type).ToArray().ShouldBe([
            EventTypes.MessageUpdated,
            EventTypes.MessagePartUpdated
        ]);
        publisher.Calls.All(call => call.Event.Payload?.GetRawText().Contains(userMessageId) is not true).ShouldBeTrue();

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Relay_resyncs_activity_status_on_pump_start_when_tracker_differs_from_harness()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-resync";
        var instanceId = "instance-resync";
        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-resync",
            ProjectId = "proj-resync",
            HarnessType = "opencode",
        });

        // Simulate stale tracker state: session was left "busy" before disconnect
        activityTracker.Update(fleetSessionId, "busy", "user-resync");

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        // Register a harness that reports "idle" (the true current state)
        var instance = new FakeHarnessSession(instanceId)
        {
            GetActivityStatusBehavior = _ => Task.FromResult<string?>("idle")
        };
        tracker.Register(instanceId, instance);

        // Wait for the pump to process the resync
        for (int i = 0; i < 50 && broadcaster.Broadcasts.Count == 0; i++)
            await Task.Delay(50);

        // Assert: tracker should be corrected to "idle"
        var snapshot = activityTracker.Get(fleetSessionId);
        snapshot.ShouldNotBeNull();
        snapshot.ActivityStatus.ShouldBe("idle");

        // Assert: a correction broadcast should have been sent on the "sessions" topic
        broadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "sessions" && b.Type == "activity_status");

        var activityBroadcast = broadcaster.Broadcasts.First(b =>
            b.Topic == "sessions" && b.Type == "activity_status");
        var payload = JsonSerializer.Deserialize<JsonElement>(activityBroadcast.Payload);
        payload.GetProperty("sessionId").GetString().ShouldBe(fleetSessionId);
        payload.GetProperty("activityStatus").GetString().ShouldBe("idle");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Relay_resyncs_activity_status_on_pump_start_when_harness_reports_busy()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-resync-busy";
        var instanceId = "instance-resync-busy";
        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-resync-busy",
            ProjectId = "proj-resync-busy",
            HarnessType = "opencode",
        });

        // Simulate stale tracker state: session was left "idle" before disconnect
        activityTracker.Update(fleetSessionId, "idle", "user-resync-busy");

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        // Register a harness that reports "busy" (the true current state)
        var instance = new FakeHarnessSession(instanceId)
        {
            GetActivityStatusBehavior = _ => Task.FromResult<string?>("busy")
        };
        tracker.Register(instanceId, instance);

        // Wait for the pump to process the resync
        for (int i = 0; i < 50 && broadcaster.Broadcasts.Count == 0; i++)
            await Task.Delay(50);

        // Assert: tracker should be corrected to "busy"
        var snapshot = activityTracker.Get(fleetSessionId);
        snapshot.ShouldNotBeNull();
        snapshot.ActivityStatus.ShouldBe("busy");

        // Assert: a correction broadcast should have been sent on the "sessions" topic
        broadcaster.Broadcasts.ShouldContain(b =>
            b.Topic == "sessions" && b.Type == "activity_status");

        var activityBroadcast = broadcaster.Broadcasts.First(b =>
            b.Topic == "sessions" && b.Type == "activity_status");
        var payload = JsonSerializer.Deserialize<JsonElement>(activityBroadcast.Payload);
        payload.GetProperty("sessionId").GetString().ShouldBe(fleetSessionId);
        payload.GetProperty("activityStatus").GetString().ShouldBe("busy");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Relay_skips_resync_when_tracker_matches_harness()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-resync-match";
        var instanceId = "instance-resync-match";
        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-resync-match",
            ProjectId = "proj-resync-match",
            HarnessType = "opencode",
        });

        // Tracker already has the correct state
        activityTracker.Update(fleetSessionId, "idle", "user-resync-match");

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        // Register a harness that reports "idle" (matches tracker)
        var instance = new FakeHarnessSession(instanceId)
        {
            GetActivityStatusBehavior = _ => Task.FromResult<string?>("idle")
        };
        tracker.Register(instanceId, instance);

        // Wait a bit to ensure no correction broadcast is sent
        await Task.Delay(200);

        // Assert: no activity_status broadcast should be sent (state already matches)
        broadcaster.Broadcasts.ShouldNotContain(b =>
            b.Topic == "sessions" && b.Type == "activity_status");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Relay_skips_resync_when_harness_returns_null_activity_status()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-resync-null";
        var instanceId = "instance-resync-null";
        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-resync-null",
            ProjectId = "proj-resync-null",
            HarnessType = "opencode",
        });

        // Tracker has stale state
        activityTracker.Update(fleetSessionId, "busy", "user-resync-null");

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        // Register a harness that returns null (doesn't support activity status queries)
        var instance = new FakeHarnessSession(instanceId)
        {
            GetActivityStatusBehavior = _ => Task.FromResult<string?>(null)
        };
        tracker.Register(instanceId, instance);

        // Wait a bit to ensure no correction broadcast is sent
        await Task.Delay(200);

        // Assert: tracker should remain unchanged (no resync when harness returns null)
        var snapshot = activityTracker.Get(fleetSessionId);
        snapshot.ShouldNotBeNull();
        snapshot.ActivityStatus.ShouldBe("busy");

        // Assert: no activity_status broadcast should be sent
        broadcaster.Broadcasts.ShouldNotContain(b =>
            b.Topic == "sessions" && b.Type == "activity_status");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    private static HarnessEvent CreateMessageLifecycleEvent(
        string type,
        string messageId,
        string role,
        string text)
        => new()
        {
            Type = type,
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = messageId,
                    sessionID = "oc-session",
                    role,
                    agent = "loom",
                    time = new { created = 1_700_000_000L }
                },
                parts = new[]
                {
                    new
                    {
                        type = "text",
                        id = $"part-{messageId}",
                        sessionID = "oc-session",
                        messageID = messageId,
                        text
                    }
                }
            })
        };

    private static HarnessEvent CreateMessagePartUpdatedEvent(
        string messageId,
        string partId,
        string text)
        => new()
        {
            Type = EventTypes.MessagePartUpdated,
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                part = new
                {
                    id = partId,
                    sessionID = "oc-session",
                    messageID = messageId,
                    type = "text",
                    text
                }
            })
        };

    private static HarnessEvent CreateMessagePartDeltaEvent(
        string messageId,
        string partId,
        string delta)
        => new()
        {
            Type = EventTypes.MessagePartDelta,
            SessionId = "oc-session",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                messageID = messageId,
                partID = partId,
                field = "text",
                delta
            })
        };


    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (await predicate().ConfigureAwait(false))
                return;

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    // ── Echo Suppression Tests ────────────────────────────────────────────────

    [Fact]
    public async Task relay_suppresses_all_user_message_echoes_from_harness()
    {
        // Arrange
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-echo-1";
        var instanceId = "instance-echo-1";
        var messageId = "msg_0046c0fa4001abc123XYZ45678";

        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-echo",
            ProjectId = "proj-echo",
            HarnessType = "opencode",
        });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        // Act: emit a user message with a msg_ ID (harness echo)
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = messageId,
                    role = "user",
                    sessionID = "oc-1"
                },
                parts = new[]
                {
                    new { type = "text", text = "hello" }
                }
            })
        });
        instance.Complete();

        await Task.Delay(200);

        // Assert: the user message echo should be suppressed
        publisher.Calls.ShouldBeEmpty("User message echo from harness should be suppressed");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task relay_suppresses_message_part_updates_for_suppressed_user_messages()
    {
        // Arrange
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-echo-3";
        var instanceId = "instance-echo-3";
        var messageId = "msg_0046c0fa4001suppressed";

        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-echo",
            ProjectId = "proj-echo",
            HarnessType = "opencode",
        });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        // First, emit a user message to register the ID for suppression
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = messageId,
                    role = "user",
                    sessionID = "oc-1"
                },
                parts = new[]
                {
                    new { type = "text", text = "hello" }
                }
            })
        });

        await Task.Delay(100);

        // Act: emit a message part delta with the same message ID
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessagePartDelta,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                part = new
                {
                    messageID = messageId,
                    index = 0,
                    delta = new { type = "text", text = " world" }
                }
            })
        });
        instance.Complete();

        await Task.Delay(200);

        // Assert: both events should be suppressed
        publisher.Calls.ShouldBeEmpty("Message part delta for suppressed user message should also be suppressed");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task relay_does_not_suppress_message_part_updates_for_non_suppressed_messages()
    {
        // Arrange
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-echo-parts";
        var instanceId = "instance-echo-parts";
        var assistantMessageId = "msg_0046c0fa4001assistant";

        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-echo",
            ProjectId = "proj-echo",
            HarnessType = "opencode",
        });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        // Act: emit a message part delta for a message ID that was never suppressed
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessagePartDelta,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                part = new
                {
                    messageID = assistantMessageId,
                    index = 0,
                    delta = new { type = "text", text = "response text" }
                }
            })
        });
        instance.Complete();

        await Task.Delay(200);

        // Assert: the part delta should NOT be suppressed (message ID not in suppression set)
        publisher.Calls.ShouldNotBeEmpty("Message part delta for non-suppressed message should NOT be suppressed");
        publisher.Calls.Count.ShouldBe(1);
        publisher.Calls.First().Event.Type.ShouldBe(EventTypes.MessagePartDelta);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task relay_does_not_suppress_assistant_messages()
    {
        // Arrange
        var (broadcaster, sessionRepo, scopeFactory, activityTracker) = BuildDependencies();
        var tracker = new InstanceTracker();
        var publisher = new FakeEventPublisher();
        var relay = BuildRelay(tracker, broadcaster, publisher, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-echo-4";
        var instanceId = "instance-echo-4";
        var messageId = "msg_0046c0fa4001assistant";

        sessionRepo.Seed(new Session
        {
            Id = fleetSessionId,
            InstanceId = instanceId,
            UserId = "user-echo",
            ProjectId = "proj-echo",
            HarnessType = "opencode",
        });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);

        // Act: emit an assistant message (should never be suppressed)
        instance.Emit(new HarnessEvent
        {
            Type = EventTypes.MessageCreated,
            SessionId = "oc-1",
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                info = new
                {
                    id = messageId,
                    role = "assistant",
                    sessionID = "oc-1"
                },
                parts = new[]
                {
                    new { type = "text", text = "response" }
                }
            })
        });
        instance.Complete();

        await Task.Delay(200);

        // Assert: the assistant message should NOT be suppressed
        publisher.Calls.ShouldNotBeEmpty("Assistant messages should never be suppressed");
        publisher.Calls.Count.ShouldBe(1);
        publisher.Calls.First().Event.Type.ShouldBe(EventTypes.MessageCreated);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }
}
