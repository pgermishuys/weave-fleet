using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using WeaveFleet.Application.Projections;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.EventBus;

public sealed class InProcessEventStoreTests
{
    [Fact]
    public async Task append_returns_positive_id_for_new_message()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var env = MakeEnvelope("msg-1");
            var id = store.Append(env);
            id.ShouldBeGreaterThan(0L);
        }
    }

    [Fact]
    public async Task append_returns_zero_for_duplicate_message_id()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var env = MakeEnvelope("msg-dup");
            var id1 = store.Append(env);
            var id2 = store.Append(env);
            id1.ShouldBeGreaterThan(0L);
            id2.ShouldBe(0L);
        }
    }

    [Fact]
    public async Task read_pending_returns_inserted_row()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var env = MakeEnvelope("msg-read");
            var id = store.Append(env);

            var rows = store.ReadPending(0);
            rows.Count.ShouldBe(1);
            rows[0].Id.ShouldBe(id);
            rows[0].Envelope.MessageId.ShouldBe("msg-read");
            rows[0].Envelope.EventType.ShouldBe(EventTypes.MessageCreated);
            rows[0].Envelope.EventId.ShouldBeNull();
        }
    }

    [Fact]
    public async Task mark_dispatched_removes_row_from_pending()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var id = store.Append(MakeEnvelope("msg-dispatch"));

            store.MarkDispatched(id);

            var rows = store.ReadPending(0);
            rows.Count.ShouldBe(0);
        }
    }

    [Fact]
    public async Task read_pending_respects_after_id()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var id1 = store.Append(MakeEnvelope("msg-a"));
            var id2 = store.Append(MakeEnvelope("msg-b"));

            var rows = store.ReadPending(id1);
            rows.Count.ShouldBe(1);
            rows[0].Id.ShouldBe(id2);
        }
    }

    [Fact]
    public async Task read_pending_tolerates_event_id_gaps()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var id1 = store.Append(MakeEnvelope("msg-gap-a"));
            var id2 = store.Append(MakeEnvelope("msg-gap-b"));
            var id3 = store.Append(MakeEnvelope("msg-gap-c"));
            store.MarkDispatched(id2);

            var rows = store.ReadPending(id1);

            rows.Count.ShouldBe(1);
            rows[0].Id.ShouldBe(id3);
        }
    }

    [Fact]
    public async Task read_pending_after_event_id_returns_only_later_events()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            _ = store.Append(MakeEnvelope("msg-before-cursor"));
            var cursor = store.Append(MakeEnvelope("msg-at-cursor"));
            var afterCursor = store.Append(MakeEnvelope("msg-after-cursor"));

            var rows = store.ReadPending(cursor);

            rows.Count.ShouldBe(1);
            rows[0].Id.ShouldBe(afterCursor);
            rows.ShouldAllBe(row => row.Id > cursor);
        }
    }

    private static InProcessEnvelope MakeEnvelope(string messageId) => new(
        @event: new HarnessEvent
        {
            Type      = EventTypes.MessageCreated,
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow,
        },
        messageId:            messageId,
        tenant:               "tenant.default",
        projectId:            "proj-1",
        sessionId:            "sess-1",
        eventType:            EventTypes.MessageCreated,
        userId:               "user-1",
        harnessType:          "opencode",
        internalPumpDedupKey: 1,
        isDurable:            true);
}

public sealed class InProcessEventPublisherTests
{
    [Fact]
    public async Task durable_event_is_persisted_and_signals_projection_wakeup()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store    = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var channels = new InProcessChannels();
            var metrics  = new InProcessMetrics();
            var publisher = new InProcessEventPublisher(
                store, channels, metrics,
                NullLogger<InProcessEventPublisher>.Instance);

            var evt = new HarnessEvent
            {
                Type = EventTypes.MessageCreated,
                SessionId = "sess-pub",
                Timestamp = DateTimeOffset.UtcNow,
            };
            _ = await publisher.PublishAsync(
                evt,
                new WeaveFleet.Application.Events.EventPublishContext("sess-pub", "proj-1", "user-1", "opencode", InternalPumpDedupKey: 42),
                CancellationToken.None);

            // Store should have the event.
            var rows = store.ReadPending(0);
            rows.Count.ShouldBe(1);
            rows[0].Envelope.MessageId.ShouldBe("sess-pub:42");

            // Projection wakeup channel should have a signal.
            channels.ProjectionWakeUp.Reader.TryRead(out _).ShouldBeTrue();

            // Fan-out channel should have the envelope.
            channels.FanOut.Reader.TryRead(out var fanOutEnv).ShouldBeTrue();
            fanOutEnv!.EventType.ShouldBe(EventTypes.MessageCreated);
            fanOutEnv.EventId.ShouldBe(rows[0].Id);
        }
    }

    [Fact]
    public async Task ephemeral_event_only_goes_to_fanout_not_store()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store    = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var channels = new InProcessChannels();
            var metrics  = new InProcessMetrics();
            var publisher = new InProcessEventPublisher(
                store, channels, metrics,
                NullLogger<InProcessEventPublisher>.Instance);

            var evt = new HarnessEvent
            {
                Type = EventTypes.SessionStatus,
                SessionId = "sess-eph",
                Timestamp = DateTimeOffset.UtcNow,
            };
            _ = await publisher.PublishAsync(
                evt,
                new WeaveFleet.Application.Events.EventPublishContext("sess-eph", "proj-1", "user-1", null, InternalPumpDedupKey: 1),
                CancellationToken.None);

            // Store must be empty for ephemeral events.
            store.ReadPending(0).Count.ShouldBe(0);

            // Wakeup channel must be empty.
            channels.ProjectionWakeUp.Reader.TryRead(out _).ShouldBeFalse();

            // Fan-out must have the event.
            channels.FanOut.Reader.TryRead(out var env).ShouldBeTrue();
            env!.EventType.ShouldBe(EventTypes.SessionStatus);
            env.EventId.ShouldBeNull();
        }
    }

    [Fact]
    public async Task Should_carry_domain_event_to_fanout_channel_when_published()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var channels = new InProcessChannels();
            var metrics = new InProcessMetrics();
            var publisher = new InProcessEventPublisher(
                store, channels, metrics,
                NullLogger<InProcessEventPublisher>.Instance);

            var evt = new HarnessEvent
            {
                Type = EventTypes.SessionStatus,
                SessionId = "sess-domain",
                Timestamp = DateTimeOffset.UtcNow,
            };

            var domainEvent = new TurnStarted
            {
                Payload = new TurnStartedPayload
                {
                    SessionId = "sess-domain",
                    MessageId = "msg-1",
                    Index = 0
                }
            };

            _ = await publisher.PublishAsync(
                evt,
                new WeaveFleet.Application.Events.EventPublishContext("sess-domain", "proj-1", "user-1", null, InternalPumpDedupKey: 3)
                {
                    DomainEvent = domainEvent
                },
                CancellationToken.None);

            channels.FanOut.Reader.TryRead(out var env).ShouldBeTrue();
            env!.DomainEvent.ShouldBe(domainEvent);
        }
    }

    [Fact]
    public async Task duplicate_durable_event_is_dropped_silently()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store    = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var channels = new InProcessChannels();
            var metrics  = new InProcessMetrics();
            var publisher = new InProcessEventPublisher(
                store, channels, metrics,
                NullLogger<InProcessEventPublisher>.Instance);

            var ctx = new WeaveFleet.Application.Events.EventPublishContext("sess-dd", "proj-1", null, null, InternalPumpDedupKey: 7);
            var evt = new HarnessEvent { Type = EventTypes.MessageCreated, SessionId = "sess-dd", Timestamp = DateTimeOffset.UtcNow };

            _ = await publisher.PublishAsync(evt, ctx, CancellationToken.None);
            _ = await publisher.PublishAsync(evt, ctx, CancellationToken.None); // duplicate

            store.ReadPending(0).Count.ShouldBe(1); // only one row
        }
    }

    [Fact]
    public async Task duplicate_correlation_id_is_idempotent()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var channels = new InProcessChannels();
            var metrics = new InProcessMetrics();
            var publisher = new InProcessEventPublisher(
                store, channels, metrics,
                NullLogger<InProcessEventPublisher>.Instance);

            var evt = new HarnessEvent { Type = EventTypes.UserPromptCommitted, SessionId = "sess-corr", Timestamp = DateTimeOffset.UtcNow };
            var first = await publisher.PublishAsync(
                evt,
                new WeaveFleet.Application.Events.EventPublishContext("sess-corr", "proj-1", "user-1", "opencode", InternalPumpDedupKey: 0)
                {
                    CorrelationId = "corr-duplicate"
                },
                CancellationToken.None);
            var second = await publisher.PublishAsync(
                evt,
                new WeaveFleet.Application.Events.EventPublishContext("sess-corr", "proj-1", "user-1", "opencode", InternalPumpDedupKey: 0)
                {
                    CorrelationId = "corr-duplicate"
                },
                CancellationToken.None);

            first.IsDuplicate.ShouldBeFalse();
            second.IsDuplicate.ShouldBeTrue();
            second.EventId.ShouldBe(first.EventId);
            store.ReadPending(0).Count.ShouldBe(1);
            channels.FanOut.Reader.TryRead(out var firstEnvelope).ShouldBeTrue();
            firstEnvelope!.EventId.ShouldBe(first.EventId);
            channels.FanOut.Reader.TryRead(out _).ShouldBeFalse();
        }
    }
}

public sealed class InProcessProjectionHostTests
{
    [Fact]
    public async Task dispatches_pending_events_to_projections()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        await using (keeper)
        {
            var store    = new InProcessEventStore(factory, NullLogger<InProcessEventStore>.Instance);
            var channels = new InProcessChannels();

            var env = new InProcessEnvelope(
                @event: new HarnessEvent
                {
                    Type = EventTypes.MessageCreated,
                    SessionId = "sess-ph",
                    Timestamp = DateTimeOffset.UtcNow,
                },
                messageId:            "sess-ph:1",
                tenant:               "tenant.default",
                projectId:            "proj-1",
                sessionId:            "sess-ph",
                eventType:            EventTypes.MessageCreated,
                userId:               "user-1",
                harnessType:          "opencode",
                internalPumpDedupKey: 1,
                isDurable:            true);
            store.Append(env);

            var projection = new RecordingProjection();
            var services = new ServiceCollection();
            services.AddScoped<RecordingProjection>(_ => projection);
            var sp = services.BuildServiceProvider();

            var registry = new WeaveFleet.Infrastructure.EventBus.ProjectionRegistry(
                [new WeaveFleet.Infrastructure.EventBus.ProjectionRegistryEntry(typeof(RecordingProjection), WeaveFleet.Infrastructure.EventBus.ConsumerScope.Cluster)]);

            var host = new InProcessProjectionHost(
                store, channels, registry,
                new InProcessMetrics(), sp,
                NullLogger<InProcessProjectionHost>.Instance);

            // Signal wake-up so the host doesn't block waiting.
            channels.ProjectionWakeUp.Writer.TryWrite(null!);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Run host in background; wait until projection fires, then cancel.
            var runTask = host.StartAsync(cts.Token);
            await projection.ReceivedOne.WaitAsync(cts.Token);
            // Allow DispatchAsync to finish MarkDispatched after projection returns.
            await Task.Delay(50, CancellationToken.None);
            cts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { }

            projection.Received.Count.ShouldBe(1);
            projection.Received[0].evt.Type.ShouldBe(EventTypes.MessageCreated);

            // Event should be marked dispatched.
            store.ReadPending(0).Count.ShouldBe(0);
        }
    }

    private sealed class RecordingProjection : IProjection<HarnessEvent>
    {
        public string Name => "recording";
        public List<(HarnessEvent evt, ProjectionContext ctx)> Received { get; } = new();
        public SemaphoreSlim ReceivedOne { get; } = new(0, 1);
        public Task HandleAsync(HarnessEvent evt, ProjectionContext ctx, CancellationToken ct)
        {
            Received.Add((evt, ctx));
            ReceivedOne.Release();
            return Task.CompletedTask;
        }
    }
}

public sealed class InProcessFanOutServiceTests
{
    [Fact]
    public async Task session_status_broadcasts_include_capabilities_on_status_change()
    {
        var channels = new InProcessChannels();
        var broadcaster = new FakeEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(new Session
        {
            Id = "sess-status-capabilities",
            InstanceId = "inst-status-capabilities",
            LifecycleStatus = "running",
            RetentionStatus = "active",
            RuntimeMode = "manual",
            ActivityStatus = "idle",
            UserId = "user-1"
        });
        var instanceTracker = new InstanceTracker();
        await using var liveSession = new FakeHarnessSession("inst-status-capabilities");
        instanceTracker.Register("inst-status-capabilities", liveSession);
        var services = new ServiceCollection();
        services.AddSingleton<IHarnessEventPersister, NoOpHarnessEventPersister>();
        services.AddSingleton(sessionRepository);
        services.AddSingleton<WeaveFleet.Domain.Repositories.ISessionRepository>(sessionRepository);
        services.AddSingleton(new SessionCapabilitiesResolver(instanceTracker));

        await using var serviceProvider = services.BuildServiceProvider();
        var service = new InProcessFanOutService(
            channels,
            broadcaster,
            new SessionActivityTracker(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InProcessFanOutService>.Instance);

        await service.StartAsync(cts.Token);
        try
        {
            channels.FanOut.Writer.TryWrite(new InProcessEnvelope(
                @event: new HarnessEvent
                {
                    Type = EventTypes.SessionStatus,
                    SessionId = "oc-status-capabilities",
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" } })
                },
                messageId: "sess-status-capabilities:1",
                tenant: "tenant.default",
                projectId: "proj-1",
                sessionId: "sess-status-capabilities",
                eventType: EventTypes.SessionStatus,
                userId: "user-1",
                harnessType: "opencode",
                internalPumpDedupKey: 1,
                isDurable: false)).ShouldBeTrue();

            await WaitForBroadcastsAsync(broadcaster, expectedCount: 2, cts.Token);

            var statusBroadcast = broadcaster.Broadcasts.Single(record =>
                record.Topic == "session:sess-status-capabilities"
                && record.Type == EventTypes.SessionStatus);
            statusBroadcast.Payload.GetProperty("capabilities").GetProperty("canAbort").GetBoolean().ShouldBeTrue();
            statusBroadcast.Payload.GetProperty("capabilities").GetProperty("canPrompt").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task activity_status_broadcasts_include_capabilities_on_status_change()
    {
        var channels = new InProcessChannels();
        var broadcaster = new FakeEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sessionRepository = new InMemorySessionRepository();
        sessionRepository.Seed(new Session
        {
            Id = "sess-activity-capabilities",
            InstanceId = "inst-activity-capabilities",
            LifecycleStatus = "running",
            RetentionStatus = "active",
            RuntimeMode = "manual",
            ActivityStatus = "idle",
            UserId = "user-1"
        });
        var instanceTracker = new InstanceTracker();
        await using var liveSession = new FakeHarnessSession("inst-activity-capabilities");
        instanceTracker.Register("inst-activity-capabilities", liveSession);
        var services = new ServiceCollection();
        services.AddSingleton<IHarnessEventPersister, NoOpHarnessEventPersister>();
        services.AddSingleton(sessionRepository);
        services.AddSingleton<WeaveFleet.Domain.Repositories.ISessionRepository>(sessionRepository);
        services.AddSingleton(new SessionCapabilitiesResolver(instanceTracker));

        await using var serviceProvider = services.BuildServiceProvider();
        var service = new InProcessFanOutService(
            channels,
            broadcaster,
            new SessionActivityTracker(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InProcessFanOutService>.Instance);

        await service.StartAsync(cts.Token);
        try
        {
            channels.FanOut.Writer.TryWrite(new InProcessEnvelope(
                @event: new HarnessEvent
                {
                    Type = EventTypes.SessionStatus,
                    SessionId = "oc-activity-capabilities",
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new { status = new { type = "busy" } })
                },
                messageId: "sess-activity-capabilities:1",
                tenant: "tenant.default",
                projectId: "proj-1",
                sessionId: "sess-activity-capabilities",
                eventType: EventTypes.SessionStatus,
                userId: "user-1",
                harnessType: "opencode",
                internalPumpDedupKey: 1,
                isDurable: false)).ShouldBeTrue();

            await WaitForBroadcastsAsync(broadcaster, expectedCount: 2, cts.Token);

            var activityBroadcast = broadcaster.Broadcasts.Single(record =>
                record.Topic == "sessions"
                && record.Type == "activity_status");
            activityBroadcast.Payload.GetProperty("sessionId").GetString().ShouldBe("sess-activity-capabilities");
            activityBroadcast.Payload.GetProperty("activityStatus").GetString().ShouldBe("busy");
            activityBroadcast.Payload.GetProperty("capabilities").GetProperty("canAbort").GetBoolean().ShouldBeTrue();
            activityBroadcast.Payload.GetProperty("capabilities").GetProperty("canPrompt").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task advisory_events_are_broadcast_without_event_id()
    {
        var channels = new InProcessChannels();
        using var broadcaster = new InMemoryEventBroadcaster();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        BroadcastEvent? received = null;
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in broadcaster.SubscribeAsync(["session:sess-advisory"], subscriberUserId: null, cts.Token))
            {
                received = evt;
                break;
            }
        }, cts.Token);

        while (broadcaster.SubscriberCount < 1)
            await Task.Delay(10, cts.Token);

        var services = new ServiceCollection();
        services.AddSingleton<IHarnessEventPersister, NoOpHarnessEventPersister>();

        await using var serviceProvider = services.BuildServiceProvider();
        var service = new InProcessFanOutService(
            channels,
            broadcaster,
            new SessionActivityTracker(),
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InProcessFanOutService>.Instance);

        await service.StartAsync(cts.Token);
        try
        {
            var env = new InProcessEnvelope(
                @event: new HarnessEvent
                {
                    Type = EventTypes.MessagePartDelta,
                    SessionId = "sess-advisory",
                    Timestamp = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new { text = "partial" })
                },
                messageId:            "sess-advisory:1",
                tenant:               "tenant.default",
                projectId:            "proj-1",
                sessionId:            "sess-advisory",
                eventType:            EventTypes.MessagePartDelta,
                userId:               null,
                harnessType:          "opencode",
                internalPumpDedupKey: 1,
                isDurable:            false)
            {
                EventId = 123
            };

            channels.FanOut.Writer.TryWrite(env).ShouldBeTrue();

            await subscribeTask.WaitAsync(cts.Token);

            received.ShouldNotBeNull();
            received!.Type.ShouldBe(EventTypes.MessagePartDelta);
            received.EventId.ShouldBeNull();
            received.SequenceNumber.ShouldBeNull();
        }
        finally
        {
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForBroadcastsAsync(
        FakeEventBroadcaster broadcaster,
        int expectedCount,
        CancellationToken ct)
    {
        while (broadcaster.Broadcasts.Count < expectedCount)
        {
            await Task.Delay(10, ct);
        }
    }

    private sealed class NoOpHarnessEventPersister : IHarnessEventPersister
    {
        public Task HandleAsync(string fleetSessionId, string ownerUserId, HarnessEvent evt, CancellationToken ct)
            => Task.CompletedTask;

        public Task FlushBufferedDeltasAsync(string fleetSessionId, string ownerUserId, CancellationToken ct)
            => Task.CompletedTask;

        public void BufferTextDelta(string fleetSessionId, HarnessEvent delta)
        {
        }
    }

}
