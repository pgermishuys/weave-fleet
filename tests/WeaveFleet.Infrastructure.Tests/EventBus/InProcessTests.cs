using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.Tests.Data;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.EventBus;

public sealed class InProcessEventPublisherTests
{
    [Fact]
    public async Task durable_event_goes_to_fanout_with_provisional_negative_id()
    {
        var channels = new InProcessChannels();
        var metrics  = new InProcessMetrics();
        var pipelineMetrics = new PipelineLatencyMetrics();
        var publisher = new InProcessEventPublisher(
            channels, metrics, pipelineMetrics,
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

        // Fan-out channel should have the envelope with a provisional negative ID.
        // Clients receive events immediately with provisional IDs for lower latency.
        channels.FanOut.Reader.TryRead(out var fanOutEnv).ShouldBeTrue();
        fanOutEnv!.EventType.ShouldBe(EventTypes.MessageCreated);
        fanOutEnv.EventId.ShouldNotBeNull();
        fanOutEnv.EventId!.Value.ShouldBeLessThan(0);  // Provisional negative ID
    }

    [Fact]
    public async Task ephemeral_event_goes_to_fanout_without_event_id()
    {
        var channels = new InProcessChannels();
        var metrics  = new InProcessMetrics();
        var pipelineMetrics = new PipelineLatencyMetrics();
        var publisher = new InProcessEventPublisher(
            channels, metrics, pipelineMetrics,
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

        // Fan-out must have the event.
        channels.FanOut.Reader.TryRead(out var env).ShouldBeTrue();
        env!.EventType.ShouldBe(EventTypes.SessionStatus);
        env.EventId.ShouldBeNull();
    }

    [Fact]
    public async Task Should_carry_domain_event_to_fanout_channel_when_published()
    {
        var channels = new InProcessChannels();
        var metrics = new InProcessMetrics();
        var pipelineMetrics = new PipelineLatencyMetrics();
        var publisher = new InProcessEventPublisher(
            channels, metrics, pipelineMetrics,
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
        var activityTracker = new SessionActivityTracker();
        var services = new ServiceCollection();
        services.AddSingleton(sessionRepository);
        services.AddSingleton<WeaveFleet.Domain.Repositories.ISessionRepository>(sessionRepository);
        services.AddSingleton(instanceTracker);
        services.AddSingleton(new SessionCapabilitiesResolver(instanceTracker, activityTracker));

        await using var serviceProvider = services.BuildServiceProvider();
        var service = new InProcessFanOutService(
            channels,
            broadcaster,
            new PipelineLatencyMetrics(),
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

            await WaitForBroadcastsAsync(broadcaster, expectedCount: 1, cts.Token);

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
            // ActivityStatus is no longer persisted - it's tracked in-memory
            UserId = "user-1"
        });
        var instanceTracker = new InstanceTracker();
        await using var liveSession = new FakeHarnessSession("inst-activity-capabilities");
        instanceTracker.Register("inst-activity-capabilities", liveSession);
        var activityTracker = new SessionActivityTracker();
        var services = new ServiceCollection();
        services.AddSingleton(sessionRepository);
        services.AddSingleton<WeaveFleet.Domain.Repositories.ISessionRepository>(sessionRepository);
        services.AddSingleton(instanceTracker);
        services.AddSingleton(new SessionCapabilitiesResolver(instanceTracker, activityTracker));

        await using var serviceProvider = services.BuildServiceProvider();
        var service = new InProcessFanOutService(
            channels,
            broadcaster,
            new PipelineLatencyMetrics(),
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

            await WaitForBroadcastsAsync(broadcaster, expectedCount: 1, cts.Token);

            // InProcessFanOutService broadcasts the session status event on the session-specific topic
            // with enriched capabilities. The global "sessions" topic activity_status broadcast
            // is now handled by HarnessEventRelay (not tested here).
            var sessionBroadcast = broadcaster.Broadcasts.Single(record =>
                record.Topic == "session:sess-activity-capabilities"
                && record.Type == "session.status");
            sessionBroadcast.Payload.GetProperty("status").GetProperty("type").GetString().ShouldBe("busy");
            sessionBroadcast.Payload.GetProperty("capabilities").GetProperty("canAbort").GetBoolean().ShouldBeTrue();
            sessionBroadcast.Payload.GetProperty("capabilities").GetProperty("canPrompt").GetBoolean().ShouldBeTrue();
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

        await using var serviceProvider = services.BuildServiceProvider();
        var service = new InProcessFanOutService(
            channels,
            broadcaster,
            new PipelineLatencyMetrics(),
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
}
