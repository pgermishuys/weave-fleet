using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// The relay's responsibility is publish-only (plus reasoning-filter sanitation): every harness
/// event flows to the event publisher via <see cref="IEventPublisher"/> with a per-pump monotonic sequence.
/// Downstream consumers (MessagePersistenceProjection for durable persistence,
/// InProcessFanOutService for WebSocket fan-out) handle their own responsibilities and are
/// tested at their own layers.
/// </summary>
public sealed class HarnessEventRelayTests
{
    private static (
        FakeEventBroadcaster Broadcaster,
        InMemorySessionRepository SessionRepo,
        IServiceScopeFactory ScopeFactory,
        SessionActivityTracker ActivityTracker,
        IHarnessEventPersister Persister
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
        var deltaBuffer = new TextDeltaBuffer();
        var activityWriteService = new SessionActivityWriteService(
            connectionFactory, messageRepo, delegationRepo, sessionRepo, new InMemorySmartLinkRepository(), outboxRepo, outboxDispatcher);
        var persister = new HarnessEventPersistenceService(messageRepo, sessionRepo, activityWriteService, deltaBuffer);

        var scopeFactory = TestServiceScopeFactory.Create(services =>
        {
            services.AddLogging();
            services.AddSingleton<ISessionRepository>((ISessionRepository)sessionRepo);
            services.AddSingleton<IMessageRepository>((IMessageRepository)messageRepo);
            services.AddSingleton<IDelegationRepository>((IDelegationRepository)delegationRepo);
            services.AddSingleton<IOutboxRepository>((IOutboxRepository)outboxRepo);
            services.AddSingleton<IOutboxDispatcher>((IOutboxDispatcher)outboxDispatcher);
            services.AddSingleton<IDbConnectionFactory>((IDbConnectionFactory)connectionFactory);
            services.AddSingleton(deltaBuffer);
            services.AddSingleton(activityWriteService);
            services.AddSingleton<IHarnessEventPersister>(persister);
            services.AddTransient<DomainEventTranslator>();
        });

        return (broadcaster, sessionRepo, scopeFactory, activityTracker, persister);
    }

    private static HarnessEventRelay BuildRelay(
        InstanceTracker tracker,
        FakeEventBroadcaster broadcaster,
        FakeEventPublisher publisher,
        SessionActivityTracker activityTracker,
        IServiceScopeFactory scopeFactory)
        => new(tracker, broadcaster, publisher, activityTracker, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

    [Fact]
    public async Task Relay_publishesEveryEvent_withMonotonicSequence_andSessionMetadata()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
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
        var seqs = publisher.Calls.Select(c => c.Context.Sequence).ToArray();
        for (int i = 1; i < seqs.Length; i++) seqs[i].ShouldBeGreaterThan(seqs[i - 1]);
        var first = publisher.Calls.First();
        first.Context.FleetSessionId.ShouldBe(fleetSessionId);
        first.Context.ProjectId.ShouldBe("proj-a");
        first.Context.UserId.ShouldBe("user-x");
        first.Context.HarnessType.ShouldBe("opencode");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Relay_publishFailure_doesNotCrashPump()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
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
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
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
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
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
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
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
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
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
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
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
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
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
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
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

        for (int i = 0; i < 50 && publisher.Calls.IsEmpty; i++)
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
}
