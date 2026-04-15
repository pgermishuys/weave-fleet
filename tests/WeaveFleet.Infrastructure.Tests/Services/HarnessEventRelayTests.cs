using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
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
        SessionActivityTracker ActivityTracker,
        TaskCompletionSource<(string Topic, string Type)> BroadcastSignal
    ) BuildDependencies()
    {
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var sessionRepo = Substitute.For<ISessionRepository>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var activityTracker = new SessionActivityTracker();

        serviceProvider.GetService(typeof(ISessionRepository)).Returns(sessionRepo);
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        var broadcastSignal = new TaskCompletionSource<(string, string)>();
        broadcaster
            .When(b => b.BroadcastAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(call => broadcastSignal.TrySetResult(
                (call.ArgAt<string>(0), call.ArgAt<string>(1))));

        return (broadcaster, sessionRepo, scopeFactory, activityTracker, broadcastSignal);
    }

    private static HarnessEventRelay BuildRelay(
        InstanceTracker tracker,
        IEventBroadcaster broadcaster,
        SessionActivityTracker activityTracker,
        IServiceScopeFactory scopeFactory)
        => new(tracker, broadcaster, activityTracker, scopeFactory, NullLogger<HarnessEventRelay>.Instance);

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
        // Wait briefly then verify no activity_status events were broadcast on the session topic
        await Task.Delay(200);

        // Only the idle broadcast from the finally block should have been called (on "sessions" topic)
        await broadcaster.DidNotReceive().BroadcastAsync(
            Arg.Is<string>(t => t.StartsWith("session:")),
            Arg.Any<string>(),
            Arg.Any<object>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

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

        // Session repo always returns null
        sessionRepo.GetAnyForInstanceAsync(instanceId).Returns((Session?)null);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
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
        await broadcaster.DidNotReceive().BroadcastAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<object>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
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

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        var instance = new FakeInstance(instanceId);

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
        var broadcaster = Substitute.For<IEventBroadcaster>();
        var sessionRepo = Substitute.For<ISessionRepository>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var scope = Substitute.For<IServiceScope>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var activityTracker = new SessionActivityTracker();

        serviceProvider.GetService(typeof(ISessionRepository)).Returns(sessionRepo);
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns(scope);

        var fleetSessionId = "fleet-abc";
        var instanceId = "instance-payload-test";

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        object? capturedPayload = null;
        var payloadSignal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster
            .When(b => b.BroadcastAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                // Capture the first broadcast on the per-session topic (not the "sessions" topic)
                if (call.ArgAt<string>(0).StartsWith("session:", StringComparison.Ordinal))
                {
                    capturedPayload = call.ArgAt<object>(2);
                    payloadSignal.TrySetResult(capturedPayload);
                }
            });

        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
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

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
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

        await broadcaster.DidNotReceive().BroadcastAsync(
            Arg.Is<string>(t => t.StartsWith("session:")),
            Arg.Any<string>(),
            Arg.Any<object>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

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

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
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
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-activity-busy";
        var instanceId = "instance-activity-busy";

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "user-1" });

        // Capture the first "busy" activity_status broadcast on "sessions" topic
        var busySignal = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster
            .When(b => b.BroadcastAsync(
                "sessions", "activity_status", Arg.Any<object>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var p = call.ArgAt<object>(2);
                var json = JsonSerializer.Serialize(p);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("activityStatus", out var statusProp)
                    && statusProp.GetString() == "busy")
                {
                    busySignal.TrySetResult(p);
                }
            });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
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
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-activity-idle";
        var instanceId = "instance-activity-idle";

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "user-1" });

        // Capture the first "idle" activity_status broadcast from the event (not the finally block)
        // We use a counter to distinguish: first idle = from event, second idle = from finally
        var idleFromEventSignal = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleCount = 0;
        broadcaster
            .When(b => b.BroadcastAsync(
                "sessions", "activity_status", Arg.Any<object>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var p = call.ArgAt<object>(2);
                var json = JsonSerializer.Serialize(p);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("activityStatus", out var statusProp)
                    && statusProp.GetString() == "idle")
                {
                    if (Interlocked.Increment(ref idleCount) == 1)
                        idleFromEventSignal.TrySetResult(p);
                }
            });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
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
        var tracker = new InstanceTracker();
        var relay = BuildRelay(tracker, broadcaster, activityTracker, scopeFactory);

        var fleetSessionId = "fleet-disconnect";
        var instanceId = "instance-disconnect";

        sessionRepo.GetAnyForInstanceAsync(instanceId)
            .Returns(new Session { Id = fleetSessionId, InstanceId = instanceId, UserId = "user-1" });

        // Pre-populate tracker with busy state
        activityTracker.Update(fleetSessionId, "busy", "user-1");

        // Capture broadcasts on "sessions" topic
        var idleSignal = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        broadcaster
            .When(b => b.BroadcastAsync(
                "sessions", "activity_status", Arg.Any<object>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var p = call.ArgAt<object>(2);
                var json = JsonSerializer.Serialize(p);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("activityStatus", out var statusProp)
                    && statusProp.GetString() == "idle")
                {
                    idleSignal.TrySetResult(p);
                }
            });

        using var cts = new CancellationTokenSource();
        await relay.StartAsync(cts.Token);
        await Task.Delay(50);

        var instance = new FakeInstance(instanceId);
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

    private sealed class FakeInstance : IHarnessSession
    {
        private readonly System.Threading.Channels.Channel<HarnessEvent> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<HarnessEvent>();

        public FakeInstance(string instanceId) { InstanceId = instanceId; }

        public string InstanceId { get; }
        public string HarnessType => "fake";
        public string? ResumeToken => null;
        public HarnessSessionStatus Status => HarnessSessionStatus.Running;

        public void Emit(HarnessEvent evt) => _channel.Writer.TryWrite(evt);
        public void Complete() => _channel.Writer.Complete();

        public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
                yield return evt;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken ct) => Task.CompletedTask;
        public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SendCommandAsync(CommandOptions options, CancellationToken ct) => Task.CompletedTask;
        public Task AbortAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct) =>
            Task.FromResult(new MessagePage([], false));
        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct) =>
            Task.FromResult(new HealthCheckResult(true, null));
        public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentInfo>>([]);
        public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CommandInfo>>([]);
        public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderInfo>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
