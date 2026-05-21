using System.Data;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Projections;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Infrastructure.Tests.Data.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// The relay's responsibility is publish-only (plus reasoning-filter sanitation): every harness
/// event flows to the event publisher via <see cref="IEventPublisher"/> with an internal per-pump dedup key.
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
    public async Task relay_publishes_every_event_with_internal_pump_dedup_key_and_session_metadata()
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
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
        var tracker = new InstanceTracker();
        var channels = new InProcessChannels();
        var metrics = new InProcessMetrics();

        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
        var publisher = new InProcessEventPublisher(
            store,
            channels,
            metrics,
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

        await WaitUntilAsync(() => Task.FromResult(store.ReadPending(0).Count == 1), cts.Token);
        await WaitUntilAsync(() => Task.FromResult(broadcaster.Broadcasts.Any(b => b.Topic == "sessions")), cts.Token);

        var secondPump = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, secondPump);
        secondPump.Emit(CreateMessageLifecycleEvent(EventTypes.MessageUpdated, "msg-after-restart", "assistant", "after restart"));
        secondPump.Complete();

        await WaitUntilAsync(() => Task.FromResult(store.ReadPending(0).Count == 2), cts.Token);

        var eventIds = store.ReadPending(0).Select(row => row.Id).ToArray();
        eventIds.Length.ShouldBe(2);
        eventIds[0].ShouldBeGreaterThan(0L);
        eventIds[1].ShouldBeGreaterThan(eventIds[0]);

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task relay_internal_dedup_keys_are_not_frontend_session_stream_cursors()
    {
        var (broadcaster, sessionRepo, scopeFactory, activityTracker, _) = BuildDependencies();
        var tracker = new InstanceTracker();
        var channels = new InProcessChannels();
        var metrics = new InProcessMetrics();
        var sharedFleetSessionId = "fleet-shared-stream";
        var sharedTopic = $"session:{sharedFleetSessionId}";

        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
        var publisher = new InProcessEventPublisher(
            store,
            channels,
            metrics,
            NullLogger<InProcessEventPublisher>.Instance);
        var fanOut = new InProcessFanOutService(
            channels,
            broadcaster,
            activityTracker,
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

        for (int i = 0; i < 100 && broadcaster.Broadcasts.Count(b => b.Topic == sharedTopic) < 2; i++)
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

    [Fact]
    public async Task relay_projection_writes_durable_assistant_replay_log_and_snapshot_while_user_echo_remains_suppressed()
    {
        const string fleetSessionId = "fleet-durable-parity";
        const string instanceId = "instance-durable-parity";
        const string userId = TestUserContext.DefaultUserId;
        const string userMessageId = "msg-user-echo";
        const string assistantMessageId = "msg-assistant-final";

        var userContext = new TestUserContext(userId);
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(
            factory,
            userId,
            sessionId: fleetSessionId,
            instanceId: instanceId,
            projectId: "proj-durable-parity");

        var messageRepository = new MessageRepository(factory, userContext);
        var sessionRepository = new SessionRepository(factory, userContext);
        var delegationRepository = new DelegationRepository(factory, userContext);
        var outboxRepository = new OutboxRepository(factory, userContext);
        var smartLinkRepository = new SmartLinkRepository(factory, userContext);
        var outboxDispatcher = new FakeOutboxDispatcher();
        var activityWriteService = new SessionActivityWriteService(
            factory,
            messageRepository,
            delegationRepository,
            sessionRepository,
            smartLinkRepository,
            outboxRepository,
            outboxDispatcher);
        var deltaBuffer = new TextDeltaBuffer();
        var persister = new HarnessEventPersistenceService(
            messageRepository,
            sessionRepository,
            activityWriteService,
            deltaBuffer);
        var logRepository = new HarnessEventLogRepository(factory, userContext);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton<ISessionRepository>(sessionRepository);
        services.AddSingleton<IMessageRepository>(messageRepository);
        services.AddSingleton<IDelegationRepository>(delegationRepository);
        services.AddSingleton<IOutboxRepository>(outboxRepository);
        services.AddSingleton<ISmartLinkRepository>(smartLinkRepository);
        services.AddSingleton<IOutboxDispatcher>(outboxDispatcher);
        services.AddSingleton<IDbConnectionFactory>(factory);
        services.AddSingleton(activityWriteService);
        services.AddSingleton(deltaBuffer);
        services.AddSingleton<IHarnessEventPersister>(persister);
        services.AddScoped<IHarnessEventLogRepository>(_ => logRepository);
        services.AddScoped<MessagePersistenceProjection>();
        services.AddTransient<DomainEventTranslator>();
        var serviceProvider = services.BuildServiceProvider();

        var tracker = new InstanceTracker();
        var broadcaster = new FakeEventBroadcaster();
        var channels = new InProcessChannels();
        var metrics = new InProcessMetrics();
        var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
        var publisher = new InProcessEventPublisher(
            store,
            channels,
            metrics,
            NullLogger<InProcessEventPublisher>.Instance);
        var activityTracker = new SessionActivityTracker();
        var fanOut = new InProcessFanOutService(
            channels,
            broadcaster,
            activityTracker,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InProcessFanOutService>.Instance);
        var projectionHost = new InProcessProjectionHost(
            store,
            channels,
            new ProjectionRegistry([
                new ProjectionRegistryEntry(typeof(MessagePersistenceProjection), ConsumerScope.Cluster)
            ]),
            metrics,
            serviceProvider,
            NullLogger<InProcessProjectionHost>.Instance);
        var relay = new HarnessEventRelay(
            tracker,
            broadcaster,
            publisher,
            activityTracker,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<HarnessEventRelay>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await fanOut.StartAsync(cts.Token);
        await projectionHost.StartAsync(cts.Token);
        await relay.StartAsync(cts.Token);
        await Task.Delay(50, CancellationToken.None);

        var instance = new FakeHarnessSession(instanceId);
        tracker.Register(instanceId, instance);
        await Task.Delay(100, CancellationToken.None);

        instance.Emit(CreateMessageLifecycleEvent(EventTypes.MessageCreated, userMessageId, "user", "user prompt"));
        instance.Emit(CreateMessageLifecycleEvent(EventTypes.MessageUpdated, userMessageId, "user", "user prompt"));
        instance.Emit(CreateMessagePartUpdatedEvent(userMessageId, "part-user", "user prompt"));
        instance.Emit(CreateMessageLifecycleEvent(EventTypes.MessageUpdated, assistantMessageId, "assistant", "draft"));
        instance.Emit(CreateMessagePartUpdatedEvent(assistantMessageId, "part-assistant", "final assistant reply"));
        instance.Complete();

        await WaitUntilAsync(async () =>
        {
            var entries = await logRepository.GetBySessionAfterAsync(fleetSessionId, 0, 10);
            var persisted = await messageRepository.GetByIdAsync(assistantMessageId, fleetSessionId);
            return entries.Count == 2
                && persisted is not null
                && persisted.PartsJson.Contains("final assistant reply", StringComparison.Ordinal);
        }, cts.Token);

        var replayLog = await logRepository.GetBySessionAfterAsync(fleetSessionId, 0, 10);
        replayLog.Select(entry => entry.SequenceNumber).ShouldBe([1L, 2L]);
        replayLog.Select(entry => entry.Type).ShouldBe([
            EventTypes.MessageUpdated,
            EventTypes.MessagePartUpdated
        ]);
        // harness_events is append-only for events that survive user-echo suppression. Suppressed
        // role=user message.created/message.updated echoes and their parts are not logged or replayed.
        replayLog.ShouldAllBe(entry => entry.Payload.Contains(assistantMessageId, StringComparison.Ordinal));
        replayLog.ShouldAllBe(entry => !entry.Payload.Contains(userMessageId, StringComparison.Ordinal));

        (await messageRepository.GetByIdAsync(userMessageId, fleetSessionId)).ShouldBeNull();
        var assistantMessage = await messageRepository.GetByIdAsync(assistantMessageId, fleetSessionId);
        assistantMessage.ShouldNotBeNull();
        assistantMessage.Role.ShouldBe("assistant");
        assistantMessage.PartsJson.ShouldContain("final assistant reply");

        var snapshot = await new SessionSnapshotBuilder(factory, userContext, activityTracker)
            .BuildAsync(fleetSessionId);
        snapshot.LastEventId.ShouldBe(2L);
        snapshot.LastSequenceNumber.ShouldBe(snapshot.LastEventId);
        snapshot.Messages.ShouldHaveSingleItem();
        snapshot.Messages[0].Info.Id.ShouldBe(assistantMessageId);
        snapshot.Messages[0].Info.Role.ShouldBe("assistant");
        snapshot.Messages[0].Parts.ShouldHaveSingleItem();
        snapshot.Messages[0].Parts[0].ShouldBeOfType<TextMessageEventPart>().Text.ShouldBe("final assistant reply");

        await cts.CancelAsync();
        await relay.StopAsync(CancellationToken.None);
        await projectionHost.StopAsync(CancellationToken.None);
        await fanOut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task buffered_deltas_should_merge_into_persisted_message_when_existing_row_exists_during_disconnect_flush()
    {
        const string fleetSessionId = "fleet-delta-disconnect-existing";
        const string instanceId = "instance-delta-disconnect-existing";
        const string userId = TestUserContext.DefaultUserId;
        const string messageId = "msg-delta-disconnect-existing";
        const string partId = "part-delta-disconnect-existing";

        var userContext = new TestUserContext(userId);
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(
            factory,
            userId,
            sessionId: fleetSessionId,
            instanceId: instanceId,
            projectId: "proj-delta-disconnect-existing");

        var messageRepository = new MessageRepository(factory, userContext);
        var sessionRepository = new SessionRepository(factory, userContext);
        var delegationRepository = new DelegationRepository(factory, userContext);
        var outboxRepository = new OutboxRepository(factory, userContext);
        var smartLinkRepository = new SmartLinkRepository(factory, userContext);
        var deltaBuffer = new TextDeltaBuffer();
        var persister = new HarnessEventPersistenceService(
            messageRepository,
            sessionRepository,
            new SessionActivityWriteService(
                factory,
                messageRepository,
                delegationRepository,
                sessionRepository,
                smartLinkRepository,
                outboxRepository,
                new FakeOutboxDispatcher()),
            deltaBuffer);

        await messageRepository.UpsertAsync(new PersistedMessage
        {
            Id = messageId,
            SessionId = fleetSessionId,
            Role = "assistant",
            PartsJson = "[]",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        persister.BufferTextDelta(fleetSessionId, CreateMessagePartDeltaEvent(messageId, partId, "partial "));
        persister.BufferTextDelta(fleetSessionId, CreateMessagePartDeltaEvent(messageId, partId, "reply"));

        await persister.FlushBufferedDeltasAsync(fleetSessionId, userId, CancellationToken.None);

        var persisted = await messageRepository.GetByIdAsync(messageId, fleetSessionId);
        persisted.ShouldNotBeNull();
        persisted.PartsJson.ShouldContain("partial reply");
        deltaBuffer.SnapshotSession(fleetSessionId).Count.ShouldBe(0);

        await persister.FlushBufferedDeltasAsync(fleetSessionId, userId, CancellationToken.None);
        var persistedAfterSecondFlush = await messageRepository.GetByIdAsync(messageId, fleetSessionId);
        persistedAfterSecondFlush.ShouldNotBeNull();
        persistedAfterSecondFlush.PartsJson.ShouldBe(persisted.PartsJson);
    }

    [Fact]
    public async Task buffered_deltas_should_remain_unpersisted_and_buffered_when_existing_message_row_is_missing_during_disconnect_flush()
    {
        const string fleetSessionId = "fleet-delta-disconnect-missing";
        const string instanceId = "instance-delta-disconnect-missing";
        const string userId = TestUserContext.DefaultUserId;
        const string messageId = "msg-delta-disconnect-missing";
        const string partId = "part-delta-disconnect-missing";

        var userContext = new TestUserContext(userId);
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using var _ = keeper;
        await RepositoryOwnershipTestHelper.SeedOwnedSessionGraphAsync(
            factory,
            userId,
            sessionId: fleetSessionId,
            instanceId: instanceId,
            projectId: "proj-delta-disconnect-missing");

        var messageRepository = new MessageRepository(factory, userContext);
        var sessionRepository = new SessionRepository(factory, userContext);
        var delegationRepository = new DelegationRepository(factory, userContext);
        var outboxRepository = new OutboxRepository(factory, userContext);
        var smartLinkRepository = new SmartLinkRepository(factory, userContext);
        var deltaBuffer = new TextDeltaBuffer();
        var persister = new HarnessEventPersistenceService(
            messageRepository,
            sessionRepository,
            new SessionActivityWriteService(
                factory,
                messageRepository,
                delegationRepository,
                sessionRepository,
                smartLinkRepository,
                outboxRepository,
                new FakeOutboxDispatcher()),
            deltaBuffer);

        persister.BufferTextDelta(fleetSessionId, CreateMessagePartDeltaEvent(messageId, partId, "orphan "));
        persister.BufferTextDelta(fleetSessionId, CreateMessagePartDeltaEvent(messageId, partId, "delta"));

        await persister.FlushBufferedDeltasAsync(fleetSessionId, userId, CancellationToken.None);

        (await messageRepository.GetByIdAsync(messageId, fleetSessionId)).ShouldBeNull();
        var snapshot = deltaBuffer.SnapshotSession(fleetSessionId);
        snapshot.Count.ShouldBe(1);
        snapshot[(messageId, partId)].ShouldBe("orphan delta");
    }

    [Fact]
    public async Task buffered_deltas_should_be_lost_when_flush_write_fails_after_buffer_clear_current_behavior()
    {
        const string fleetSessionId = "fleet-delta-disconnect-failure";
        const string userId = TestUserContext.DefaultUserId;
        const string messageId = "msg-delta-disconnect-failure";
        const string partId = "part-delta-disconnect-failure";

        var messageRepository = new InMemoryMessageRepository();
        messageRepository.Seed(new PersistedMessage
        {
            Id = messageId,
            SessionId = fleetSessionId,
            Role = "assistant",
            PartsJson = "[]",
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
        var sessionRepository = new InMemorySessionRepository();
        var deltaBuffer = new TextDeltaBuffer();
        var persister = new HarnessEventPersistenceService(
            messageRepository,
            sessionRepository,
            new SessionActivityWriteService(
                new ThrowingDbConnectionFactory(),
                messageRepository,
                new InMemoryDelegationRepository(),
                sessionRepository,
                new InMemorySmartLinkRepository(),
                new InMemoryOutboxRepository(),
                new FakeOutboxDispatcher()),
            deltaBuffer);

        persister.BufferTextDelta(fleetSessionId, CreateMessagePartDeltaEvent(messageId, partId, "lost text"));

        await persister.FlushBufferedDeltasAsync(fleetSessionId, userId, CancellationToken.None);

        var persisted = await messageRepository.GetByIdAsync(messageId, fleetSessionId);
        persisted.ShouldNotBeNull();
        persisted.PartsJson.ShouldBe("[]");
        messageRepository.UpsertCalls.ShouldBeEmpty();
        deltaBuffer.SnapshotSession(fleetSessionId).Count.ShouldBe(0);
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

    private sealed class ThrowingDbConnectionFactory : IDbConnectionFactory
    {
        public IDbConnection CreateConnection()
            => throw new InvalidOperationException("Simulated transaction failure.");
    }

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
}
