using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode.Pooling;

public sealed class SseEventDemultiplexerTests
{
    [Fact]
    public async Task routes_events_across_directory_streams_reconnects_and_drops_unattributable_events()
    {
        var instance = CreateInstance();
        var firstConsumerId = Guid.NewGuid();
        var secondConsumerId = Guid.NewGuid();
        var thirdConsumerId = Guid.NewGuid();
        var resolver = new FakeBindingResolver();
        var streamFactory = new FakeStreamFactory();
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        resolver.Bind(instance, "/repo/one", "oc-session-1", firstConsumerId);
        resolver.Bind(instance, "/repo/one", "oc-session-2", secondConsumerId);
        resolver.Bind(instance, "/repo/two", "oc-session-3", thirdConsumerId);

        var firstConsumer = Channel.CreateUnbounded<OpenCodeSseEvent>();
        var secondConsumer = Channel.CreateUnbounded<OpenCodeSseEvent>();
        var thirdConsumer = Channel.CreateUnbounded<OpenCodeSseEvent>();

        var firstRegistration = await demultiplexer.RegisterConsumerAsync(instance, "/repo/one", firstConsumerId, firstConsumer, CancellationToken.None);
        var secondRegistration = await demultiplexer.RegisterConsumerAsync(instance, "/repo/one", secondConsumerId, secondConsumer, CancellationToken.None);
        var thirdRegistration = await demultiplexer.RegisterConsumerAsync(instance, "/repo/two", thirdConsumerId, thirdConsumer, CancellationToken.None);

        var repoOneStream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);
        var repoTwoStream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/two", 1);

        streamFactory.SubscriptionCount(instance, "/repo/one").ShouldBe(1);
        streamFactory.SubscriptionCount(instance, "/repo/two").ShouldBe(1);

        var firstEvent = CreateEvent("message.updated", "oc-session-1");
        var secondEvent = CreateEvent("part.updated", "oc-session-2");
        var thirdEvent = CreateEvent("session.updated", "oc-session-3");

        await repoOneStream.WriteAsync(firstEvent);
        await repoOneStream.WriteAsync(secondEvent);
        await repoTwoStream.WriteAsync(thirdEvent);

        (await ReadNextAsync(firstConsumer)).ShouldBeSameAs(firstEvent);
        (await ReadNextAsync(secondConsumer)).ShouldBeSameAs(secondEvent);
        (await ReadNextAsync(thirdConsumer)).ShouldBeSameAs(thirdEvent);

        await secondRegistration.DisposeAsync();

        await repoOneStream.WriteAsync(CreateEvent("part.updated", "oc-session-2"));
        await AssertNoEventAsync(secondConsumer);

        repoOneStream.Complete();
        repoOneStream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 2);

        var reconnectEvent = CreateEvent("message.updated", "oc-session-1");
        await repoOneStream.WriteAsync(reconnectEvent);

        (await ReadNextAsync(firstConsumer)).ShouldBeSameAs(reconnectEvent);

        await repoOneStream.WriteAsync(CreateEventWithoutSessionId("message.updated"));
        await repoOneStream.WriteAsync(CreateEvent("message.updated", "oc-session-missing-binding"));

        await WaitForDroppedCountAsync(demultiplexer, 3);
        demultiplexer.DroppedUnattributableEventCount.ShouldBe(3);
        await AssertNoEventAsync(firstConsumer);
        await AssertNoEventAsync(thirdConsumer);

        await firstRegistration.DisposeAsync();
        await streamFactory.WaitForCancellationAsync(instance, "/repo/one", 2);
        streamFactory.ActiveSubscriptionCount(instance, "/repo/one").ShouldBe(0);

        streamFactory.ActiveSubscriptionCount(instance, "/repo/two").ShouldBe(1);
        await thirdRegistration.DisposeAsync();
        await streamFactory.WaitForCancellationAsync(instance, "/repo/two", 1);
        streamFactory.ActiveSubscriptionCount(instance, "/repo/two").ShouldBe(0);
    }

    [Fact]
    public async Task register_waits_until_directory_stream_is_active()
    {
        var instance = CreateInstance();
        var consumerId = Guid.NewGuid();
        var resolver = new FakeBindingResolver();
        var streamFactory = new FakeStreamFactory { DelayConnectedNotification = true };
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        var channel = Channel.CreateUnbounded<OpenCodeSseEvent>();
        var registrationTask = demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            consumerId,
            channel,
            CancellationToken.None).AsTask();

        await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);
        registrationTask.IsCompleted.ShouldBeFalse();

        streamFactory.CompleteConnectedNotification();
        await using var registration = await registrationTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task register_waits_for_reconnected_stream_after_existing_stream_stops()
    {
        var instance = CreateInstance();
        var firstConsumerId = Guid.NewGuid();
        var secondConsumerId = Guid.NewGuid();
        var resolver = new FakeBindingResolver();
        var streamFactory = new FakeStreamFactory
        {
            DelayConnectedNotificationAfterFirstSubscription = true,
        };
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        await using var firstRegistration = await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            firstConsumerId,
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            CancellationToken.None);
        var firstStream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);

        firstStream.Complete();
        await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 2);
        var secondRegistrationTask = demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            secondConsumerId,
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            CancellationToken.None).AsTask();

        secondRegistrationTask.IsCompleted.ShouldBeFalse();

        streamFactory.CompleteConnectedNotification();
        await using var secondRegistration = await secondRegistrationTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task register_rejects_duplicate_consumer_negative_generation_cancellation_and_disposal()
    {
        var instance = CreateInstance();
        var resolver = new FakeBindingResolver();
        var streamFactory = new FakeStreamFactory();
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var consumerId = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<OpenCodeSseEvent>();
        await using var registration = await demultiplexer.RegisterConsumerAsync(instance, "/repo/one", consumerId, channel, CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(async () => await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            consumerId,
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            CancellationToken.None));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            Guid.NewGuid(),
            leaseGeneration: -1,
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            CancellationToken.None));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            Guid.NewGuid(),
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            cts.Token));

        await registration.DisposeAsync();
        await registration.DisposeAsync();
        await demultiplexer.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            Guid.NewGuid(),
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            CancellationToken.None));
    }

    [Fact]
    public async Task lease_generation_mismatch_and_closed_consumer_channel_are_dropped()
    {
        var instance = CreateInstance();
        var consumerId = Guid.NewGuid();
        var resolver = new FakeBindingResolver();
        var streamFactory = new FakeStreamFactory();
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var channel = Channel.CreateUnbounded<OpenCodeSseEvent>();
        await using var registration = await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            consumerId,
            leaseGeneration: 2,
            channel,
            CancellationToken.None);
        var stream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);

        resolver.Bind(instance, "/repo/one", "stale-session", consumerId, leaseGeneration: 1);
        await stream.WriteAsync(CreateEvent("message.updated", "stale-session"));

        resolver.Bind(instance, "/repo/one", "closed-channel-session", consumerId, leaseGeneration: 2);
        channel.Writer.TryComplete();
        await stream.WriteAsync(CreateEvent("message.updated", "closed-channel-session"));

        await WaitForDroppedCountAsync(demultiplexer, 2);
        demultiplexer.DroppedUnattributableEventCount.ShouldBe(2);
    }

    [Fact]
    public async Task stream_factory_failure_reconnects_and_dispose_waits_for_cancellation()
    {
        var instance = CreateInstance();
        var consumerId = Guid.NewGuid();
        var resolver = new FakeBindingResolver();
        var streamFactory = new FakeStreamFactory { ThrowBeforeFirstYield = true };
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        resolver.Bind(instance, "/repo/one", "oc-session-1", consumerId);
        var channel = Channel.CreateUnbounded<OpenCodeSseEvent>();
        await using var registration = await demultiplexer.RegisterConsumerAsync(instance, "/repo/one", consumerId, channel, CancellationToken.None);

        var stream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);
        var routedEvent = CreateEvent("message.updated", "oc-session-1");
        await stream.WriteAsync(routedEvent);

        (await ReadNextAsync(channel)).ShouldBeSameAs(routedEvent);
        streamFactory.FailureCount.ShouldBeGreaterThanOrEqualTo(1);

        await demultiplexer.DisposeAsync();
        streamFactory.ActiveSubscriptionCount(instance, "/repo/one").ShouldBe(0);
    }

    [Fact]
    public void constructor_rejects_invalid_reconnect_delays()
    {
        var resolver = new FakeBindingResolver();
        var streamFactory = new FakeStreamFactory();

        Should.Throw<ArgumentOutOfRangeException>(() => new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.FromMilliseconds(-1),
            TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.FromMilliseconds(2),
            TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task register_rejects_duplicate_negative_canceled_and_after_dispose()
    {
        var instance = CreateInstance();
        var consumerId = Guid.NewGuid();
        var resolver = new FakeBindingResolver();
        var streamFactory = new FakeStreamFactory();
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);
        var channel = Channel.CreateUnbounded<OpenCodeSseEvent>();
        await using var registration = await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            consumerId,
            leaseGeneration: 1,
            channel,
            CancellationToken.None);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            Guid.NewGuid(),
            leaseGeneration: -1,
            channel,
            CancellationToken.None));
        await Should.ThrowAsync<InvalidOperationException>(async () => await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            consumerId,
            leaseGeneration: 1,
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            CancellationToken.None));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/two",
            Guid.NewGuid(),
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            cts.Token));

        await demultiplexer.DisposeAsync();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/two",
            Guid.NewGuid(),
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            CancellationToken.None));
    }

    [Fact]
    public async Task default_http_stream_factory_registration_awaits_active_stream_and_can_be_canceled()
    {
        var instance = CreateInstance();
        var consumerId = Guid.NewGuid();
        var resolver = new FakeBindingResolver();
        await using var demultiplexer = new SseEventDemultiplexer(
            resolver,
            NullLogger<SseEventDemultiplexer>.Instance);
        resolver.Bind(instance, "/repo/one", "oc-session-1", consumerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Should.ThrowAsync<OperationCanceledException>(async () => await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            consumerId,
            Channel.CreateUnbounded<OpenCodeSseEvent>(),
            cts.Token));
    }

    private static PooledOpenCodeInstance CreateInstance()
    {
        return new PooledOpenCodeInstance(
            "key",
            "instance-1",
            processId: 123,
            shutdownAsync: () => ValueTask.CompletedTask);
    }

    private static OpenCodeSseEvent CreateEvent(string type, string sessionId)
    {
        var properties = JsonSerializer.SerializeToElement(new { sessionID = sessionId });
        return new OpenCodeSseEvent { Type = type, Properties = properties };
    }

    private static OpenCodeSseEvent CreateEventWithoutSessionId(string type)
    {
        var properties = JsonSerializer.SerializeToElement(new { status = "idle" });
        return new OpenCodeSseEvent { Type = type, Properties = properties };
    }

    private static async Task<OpenCodeSseEvent> ReadNextAsync(Channel<OpenCodeSseEvent> channel)
    {
        return await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task AssertNoEventAsync(Channel<OpenCodeSseEvent> channel)
    {
        try
        {
            var evt = await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(150));
            throw new InvalidOperationException($"Unexpected event received: {evt.Type}.");
        }
        catch (TimeoutException)
        {
        }
    }

    private static async Task WaitForDroppedCountAsync(SseEventDemultiplexer demultiplexer, long count)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (demultiplexer.DroppedUnattributableEventCount >= count)
            {
                return;
            }

            await Task.Delay(10);
        }

        demultiplexer.DroppedUnattributableEventCount.ShouldBe(count);
    }

    private sealed class FakeBindingResolver : IOpenCodeSseEventBindingResolver
    {
        private readonly ConcurrentDictionary<BindingKey, Binding> _bindings = new();

        public void Bind(PooledOpenCodeInstance instance, string directory, string openCodeSessionId, Guid consumerId)
        {
            Bind(instance, directory, openCodeSessionId, consumerId, leaseGeneration: 0);
        }

        public void Bind(PooledOpenCodeInstance instance, string directory, string openCodeSessionId, Guid consumerId, long leaseGeneration)
        {
            _bindings[new BindingKey(instance, directory, openCodeSessionId)] = new Binding(consumerId, leaseGeneration);
        }

        public bool TryResolveConsumer(
            PooledOpenCodeInstance instance,
            string directory,
            string openCodeSessionId,
            out Guid consumerId,
            out long leaseGeneration)
        {
            if (_bindings.TryGetValue(new BindingKey(instance, directory, openCodeSessionId), out var binding))
            {
                consumerId = binding.ConsumerId;
                leaseGeneration = binding.LeaseGeneration;
                return true;
            }

            consumerId = Guid.Empty;
            leaseGeneration = 0;
            return false;
        }

        private readonly record struct Binding(Guid ConsumerId, long LeaseGeneration);

        private readonly record struct BindingKey(
            PooledOpenCodeInstance Instance,
            string Directory,
            string OpenCodeSessionId);
    }

    private sealed class FakeStreamFactory : IOpenCodeSseEventStreamFactory
    {
        private readonly object _sync = new();
        private readonly Dictionary<StreamKey, List<FakeStream>> _streams = new();
        private int _failureCount;
        private int _thrownFailure;

        public bool ThrowBeforeFirstYield { get; set; }

        public bool DelayConnectedNotification { get; set; }

        public bool DelayConnectedNotificationAfterFirstSubscription { get; set; }

        public int FailureCount => Volatile.Read(ref _failureCount);

        public TaskCompletionSource ConnectedNotification { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<OpenCodeSseEvent> SubscribeAsync(
            PooledOpenCodeInstance instance,
            string directory,
            Func<Task> connectedAsync,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (ThrowBeforeFirstYield && Interlocked.Exchange(ref _thrownFailure, 1) == 0)
            {
                Interlocked.Increment(ref _failureCount);
                throw new InvalidOperationException("simulated SSE stream failure");
            }

            var stream = new FakeStream();
            lock (_sync)
            {
                var key = new StreamKey(instance, directory);
                if (!_streams.TryGetValue(key, out var streams))
                {
                    streams = [];
                    _streams[key] = streams;
                }

                streams.Add(stream);
            }

            var delayConnectedNotification = DelayConnectedNotification
                || (DelayConnectedNotificationAfterFirstSubscription && SubscriptionCount(instance, directory) > 1);
            if (delayConnectedNotification)
            {
                await ConnectedNotification.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            await connectedAsync().ConfigureAwait(false);

            await using var cancellation = ct.Register(() => stream.Cancel());
            try
            {
                await foreach (var evt in stream.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    yield return evt;
                }
            }
            finally
            {
                stream.Cancel();
            }
        }

        public int SubscriptionCount(PooledOpenCodeInstance instance, string directory)
        {
            lock (_sync)
            {
                return _streams.TryGetValue(new StreamKey(instance, directory), out var streams) ? streams.Count : 0;
            }
        }

        public int ActiveSubscriptionCount(PooledOpenCodeInstance instance, string directory)
        {
            lock (_sync)
            {
                return _streams.TryGetValue(new StreamKey(instance, directory), out var streams)
                    ? streams.Count(stream => !stream.IsCanceled)
                    : 0;
            }
        }

        public async Task<FakeStream> WaitForSubscriptionAsync(PooledOpenCodeInstance instance, string directory, int count)
        {
            var key = new StreamKey(instance, directory);
            var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < timeoutAt)
            {
                lock (_sync)
                {
                    if (_streams.TryGetValue(key, out var streams) && streams.Count >= count)
                    {
                        return streams[count - 1];
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Timed out waiting for subscription {count} for {directory}.");
        }

        public async Task WaitForCancellationAsync(PooledOpenCodeInstance instance, string directory, int count)
        {
            var stream = await WaitForSubscriptionAsync(instance, directory, count);
            await stream.Canceled.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void CompleteConnectedNotification() => ConnectedNotification.TrySetResult();

        private readonly record struct StreamKey(PooledOpenCodeInstance Instance, string Directory);
    }

    private sealed class FakeStream
    {
        private readonly Channel<OpenCodeSseEvent> _events = Channel.CreateUnbounded<OpenCodeSseEvent>();
        private readonly TaskCompletionSource _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _canceledFlag;

        public Task Canceled => _canceled.Task;

        public bool IsCanceled => Volatile.Read(ref _canceledFlag) != 0;

        public ValueTask WriteAsync(OpenCodeSseEvent evt) => _events.Writer.WriteAsync(evt);

        public void Complete() => _events.Writer.TryComplete();

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _canceledFlag, 1) == 0)
            {
                _events.Writer.TryComplete();
                _canceled.TrySetResult();
            }
        }

        public IAsyncEnumerable<OpenCodeSseEvent> ReadAllAsync(CancellationToken ct) => _events.Reader.ReadAllAsync(ct);
    }
}
