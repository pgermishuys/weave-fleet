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
    // Test double: controllable harness instance
    // -----------------------------------------------------------------------

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
