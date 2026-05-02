using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Projections;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Infrastructure.Tests.Data;

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

    private static InProcessEnvelope MakeEnvelope(string messageId) => new(
        Event: new HarnessEvent
        {
            Type      = EventTypes.MessageCreated,
            SessionId = "sess-1",
            Timestamp = DateTimeOffset.UtcNow,
        },
        MessageId:   messageId,
        Tenant:      "tenant.default",
        ProjectId:   "proj-1",
        SessionId:   "sess-1",
        EventType:   EventTypes.MessageCreated,
        UserId:      "user-1",
        HarnessType: "opencode",
        Sequence:    1,
        IsDurable:   true);
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
            await publisher.PublishAsync(
                evt,
                new WeaveFleet.Application.Events.EventPublishContext("sess-pub", "proj-1", "user-1", "opencode", Sequence: 42),
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
            await publisher.PublishAsync(
                evt,
                new WeaveFleet.Application.Events.EventPublishContext("sess-eph", "proj-1", "user-1", null, Sequence: 1),
                CancellationToken.None);

            // Store must be empty for ephemeral events.
            store.ReadPending(0).Count.ShouldBe(0);

            // Wakeup channel must be empty.
            channels.ProjectionWakeUp.Reader.TryRead(out _).ShouldBeFalse();

            // Fan-out must have the event.
            channels.FanOut.Reader.TryRead(out var env).ShouldBeTrue();
            env!.EventType.ShouldBe(EventTypes.SessionStatus);
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

            var ctx = new WeaveFleet.Application.Events.EventPublishContext("sess-dd", "proj-1", null, null, Sequence: 7);
            var evt = new HarnessEvent { Type = EventTypes.MessageCreated, SessionId = "sess-dd", Timestamp = DateTimeOffset.UtcNow };

            await publisher.PublishAsync(evt, ctx, CancellationToken.None);
            await publisher.PublishAsync(evt, ctx, CancellationToken.None); // duplicate

            store.ReadPending(0).Count.ShouldBe(1); // only one row
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
                Event: new HarnessEvent
                {
                    Type = EventTypes.MessageCreated,
                    SessionId = "sess-ph",
                    Timestamp = DateTimeOffset.UtcNow,
                },
                MessageId:   "sess-ph:1",
                Tenant:      "tenant.default",
                ProjectId:   "proj-1",
                SessionId:   "sess-ph",
                EventType:   EventTypes.MessageCreated,
                UserId:      "user-1",
                HarnessType: "opencode",
                Sequence:    1,
                IsDurable:   true);
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
