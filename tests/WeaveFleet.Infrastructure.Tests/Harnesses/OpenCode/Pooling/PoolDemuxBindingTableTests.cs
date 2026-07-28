using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode.Pooling;

public sealed class PoolDemuxBindingTableTests
{
    [Fact]
    public async Task demux_drops_unknown_stale_and_released_bindings_but_routes_valid_binding()
    {
        var instance = CreateInstance();
        var table = new PoolDemuxBindingTable();
        var streamFactory = new FakeStreamFactory();
        var staleConsumerId = Guid.NewGuid();
        var validConsumerId = Guid.NewGuid();
        await using var demultiplexer = new SseEventDemultiplexer(
            table,
            streamFactory,
            NullLogger<SseEventDemultiplexer>.Instance,
            TimeSpan.Zero,
            TimeSpan.Zero);

        var staleConsumer = Channel.CreateUnbounded<OpenCodeSseEvent>();
        await using var staleRegistration = await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            staleConsumerId,
            leaseGeneration: 1,
            staleConsumer,
            CancellationToken.None);

        var stream = await streamFactory.WaitForSubscriptionAsync(instance, "/repo/one", 1);
        table.Bind(instance, "oc-session-1", validConsumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 2);

        await stream.WriteAsync(CreateEvent("message.updated", "unknown-oc-session"));
        await stream.WriteAsync(CreateEvent("message.updated", "oc-session-1"));

        await AssertNoEventAsync(staleConsumer);
        await WaitForDroppedCountAsync(demultiplexer, 2);

        var validConsumer = Channel.CreateUnbounded<OpenCodeSseEvent>();
        await using var validRegistration = await demultiplexer.RegisterConsumerAsync(
            instance,
            "/repo/one",
            validConsumerId,
            leaseGeneration: 2,
            validConsumer,
            CancellationToken.None);

        var routedEvent = CreateEvent("message.updated", "oc-session-1");
        await stream.WriteAsync(routedEvent);

        (await ReadNextAsync(validConsumer)).ShouldBeSameAs(routedEvent);

        table.Remove(instance, "oc-session-1", leaseGeneration: 2).ShouldBeTrue();
        await stream.WriteAsync(CreateEvent("message.updated", "oc-session-1"));

        await WaitForDroppedCountAsync(demultiplexer, 3);
        await AssertNoEventAsync(validConsumer);
    }

    [Fact]
    public void resolver_rejects_directory_mismatch_and_stale_generation()
    {
        var instance = CreateInstance();
        var table = new PoolDemuxBindingTable();
        var consumerId = Guid.NewGuid();

        table.Bind(instance, "oc-session-1", consumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 3);

        table.TryGetBinding(instance, "/repo/two", "oc-session-1", 3, out _).ShouldBeFalse();
        table.TryGetBinding(instance, "/repo/one", "oc-session-1", 2, out _).ShouldBeFalse();
        table.TryResolveConsumer(instance, "/repo/two", "oc-session-1", out _, out _).ShouldBeFalse();
        table.TryResolveConsumer(instance, "/repo/one", "oc-session-1", out var resolvedConsumerId, out var leaseGeneration).ShouldBeTrue();
        resolvedConsumerId.ShouldBe(consumerId);
        leaseGeneration.ShouldBe(3);
    }

    [Fact]
    public void command_binding_verification_requires_full_backend_binding_tuple()
    {
        var instance = CreateInstance();
        var table = new PoolDemuxBindingTable();

        table.Bind(
            instance,
            "oc-session-1",
            Guid.NewGuid(),
            "fleet-session-1",
            "user-1",
            "/repo/one",
            leaseGeneration: 3);

        table.TryVerifyCommandBinding(instance, "fleet-session-1", "user-1", "oc-session-1", "/repo/one", 3, out var binding)
            .ShouldBeTrue();
        binding.OpenCodeSessionId.ShouldBe("oc-session-1");

        table.TryVerifyCommandBinding(instance, "fleet-session-2", "user-1", "oc-session-1", "/repo/one", 3, out _)
            .ShouldBeFalse();
        table.TryVerifyCommandBinding(instance, "fleet-session-1", "user-2", "oc-session-1", "/repo/one", 3, out _)
            .ShouldBeFalse();
        table.TryVerifyCommandBinding(instance, "fleet-session-1", "user-1", "oc-session-2", "/repo/one", 3, out _)
            .ShouldBeFalse();
        table.TryVerifyCommandBinding(instance, "fleet-session-1", "user-1", "oc-session-1", "/repo/two", 3, out _)
            .ShouldBeFalse();
        table.TryVerifyCommandBinding(instance, "fleet-session-1", "user-1", "oc-session-1", "/repo/one", 2, out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void newer_generation_replaces_old_binding_and_stale_generation_cannot_remove_it()
    {
        var instance = CreateInstance();
        var table = new PoolDemuxBindingTable();
        var oldConsumerId = Guid.NewGuid();
        var newConsumerId = Guid.NewGuid();

        table.Bind(instance, "oc-session-1", oldConsumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 1);
        table.Bind(instance, "oc-session-1", newConsumerId, "fleet-session-2", "user-2", "/repo/two", leaseGeneration: 2);

        table.TryResolveConsumer(instance, "/repo/one", "oc-session-1", out _, out _).ShouldBeFalse();
        table.TryResolveConsumer(instance, "/repo/two", "oc-session-1", out var resolvedConsumerId, out var leaseGeneration).ShouldBeTrue();
        resolvedConsumerId.ShouldBe(newConsumerId);
        leaseGeneration.ShouldBe(2);

        table.Remove(instance, "oc-session-1", leaseGeneration: 1).ShouldBeFalse();
        table.TryGetBinding(instance, "/repo/two", "oc-session-1", 2, out _).ShouldBeTrue();
        table.Remove(instance, "oc-session-1", leaseGeneration: 2).ShouldBeTrue();
        table.TryGetBinding(instance, "/repo/two", "oc-session-1", out _).ShouldBeFalse();
    }

    [Fact]
    public void remove_for_lease_removes_only_matching_active_lease_bindings()
    {
        var instance = CreateInstance();
        var otherInstance = CreateInstance();
        var table = new PoolDemuxBindingTable();
        var consumerId = Guid.NewGuid();
        var otherConsumerId = Guid.NewGuid();

        table.Bind(instance, "oc-session-1", consumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 3);
        table.Bind(instance, "oc-session-2", consumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 3);
        table.Bind(instance, "other-consumer", otherConsumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 3);
        table.Bind(instance, "other-fleet-session", consumerId, "fleet-session-2", "user-1", "/repo/one", leaseGeneration: 3);
        table.Bind(instance, "other-directory", consumerId, "fleet-session-1", "user-1", "/repo/two", leaseGeneration: 3);
        table.Bind(instance, "other-generation", consumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 2);
        table.Bind(otherInstance, "other-instance", consumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 3);

        var removed = table.RemoveForLease(
            instance,
            consumerId,
            "fleet-session-1",
            "/repo/one",
            leaseGeneration: 3);

        removed.ShouldBe(2);
        table.TryGetBinding(instance, "/repo/one", "oc-session-1", 3, out _).ShouldBeFalse();
        table.TryGetBinding(instance, "/repo/one", "oc-session-2", 3, out _).ShouldBeFalse();
        table.TryGetBinding(instance, "/repo/one", "other-consumer", 3, out _).ShouldBeTrue();
        table.TryGetBinding(instance, "/repo/one", "other-fleet-session", 3, out _).ShouldBeTrue();
        table.TryGetBinding(instance, "/repo/two", "other-directory", 3, out _).ShouldBeTrue();
        table.TryGetBinding(instance, "/repo/one", "other-generation", 2, out _).ShouldBeTrue();
        table.TryGetBinding(otherInstance, "/repo/one", "other-instance", 3, out _).ShouldBeTrue();
    }

    [Fact]
    public void older_generation_cannot_replace_newer_binding()
    {
        var instance = CreateInstance();
        var table = new PoolDemuxBindingTable();
        var currentConsumerId = Guid.NewGuid();

        table.Bind(instance, "oc-session-1", currentConsumerId, "fleet-session-2", "user-2", "/repo/two", leaseGeneration: 2);
        table.Bind(instance, "oc-session-1", Guid.NewGuid(), "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 1);

        table.TryResolveConsumer(instance, "/repo/two", "oc-session-1", out var resolvedConsumerId, out var leaseGeneration).ShouldBeTrue();
        resolvedConsumerId.ShouldBe(currentConsumerId);
        leaseGeneration.ShouldBe(2);
    }

    [Fact]
    public void same_generation_rebind_with_different_metadata_is_rejected_but_idempotent_rebind_succeeds()
    {
        var instance = CreateInstance();
        var table = new PoolDemuxBindingTable();
        var consumerId = Guid.NewGuid();

        table.Bind(instance, "oc-session-1", consumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 3);
        table.Bind(instance, "oc-session-1", consumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 3);

        var exception = Should.Throw<InvalidOperationException>(() => table.Bind(
            instance,
            "oc-session-1",
            Guid.NewGuid(),
            "fleet-session-1",
            "user-1",
            "/repo/one",
            leaseGeneration: 3));

        exception.Message.ShouldContain("same lease generation");
    }

    [Fact]
    public void move_bindings_transfers_only_matching_bindings_to_replacement_generation()
    {
        var sourceInstance = CreateInstance();
        var targetInstance = CreateInstance();
        var table = new PoolDemuxBindingTable();
        var movingConsumerId = Guid.NewGuid();
        var otherConsumerId = Guid.NewGuid();

        table.Bind(sourceInstance, "oc-session-1", movingConsumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 5);
        table.Bind(sourceInstance, "oc-session-2", movingConsumerId, "fleet-session-2", "user-1", "/repo/one", leaseGeneration: 5);
        table.Bind(sourceInstance, "other-consumer-session", otherConsumerId, "fleet-session-3", "user-1", "/repo/one", leaseGeneration: 5);
        table.Bind(sourceInstance, "other-directory-session", movingConsumerId, "fleet-session-4", "user-1", "/repo/two", leaseGeneration: 5);
        table.Bind(sourceInstance, "other-generation-session", movingConsumerId, "fleet-session-5", "user-1", "/repo/one", leaseGeneration: 4);

        table.MoveBindings(
            sourceInstance,
            targetInstance,
            movingConsumerId,
            "/repo/one",
            sourceLeaseGeneration: 5,
            targetLeaseGeneration: 6);

        table.TryGetBinding(sourceInstance, "/repo/one", "oc-session-1", out _).ShouldBeFalse();
        table.TryGetBinding(sourceInstance, "/repo/one", "oc-session-2", out _).ShouldBeFalse();
        table.TryGetBinding(targetInstance, "/repo/one", "oc-session-1", 5, out _).ShouldBeFalse();
        table.TryGetBinding(targetInstance, "/repo/one", "oc-session-1", 6, out var movedBinding).ShouldBeTrue();
        movedBinding.ConsumerId.ShouldBe(movingConsumerId);
        movedBinding.FleetSessionId.ShouldBe("fleet-session-1");
        movedBinding.UserId.ShouldBe("user-1");
        table.TryVerifyCommandBinding(targetInstance, "fleet-session-2", "user-1", "oc-session-2", "/repo/one", 6, out _)
            .ShouldBeTrue();

        table.TryGetBinding(sourceInstance, "/repo/one", "other-consumer-session", 5, out _).ShouldBeTrue();
        table.TryGetBinding(sourceInstance, "/repo/two", "other-directory-session", 5, out _).ShouldBeTrue();
        table.TryGetBinding(sourceInstance, "/repo/one", "other-generation-session", 4, out _).ShouldBeTrue();
        table.TryGetBinding(targetInstance, "/repo/one", "other-consumer-session", out _).ShouldBeFalse();
        table.TryGetBinding(targetInstance, "/repo/two", "other-directory-session", out _).ShouldBeFalse();
        table.TryGetBinding(targetInstance, "/repo/one", "other-generation-session", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task move_bindings_restores_resolution_after_source_crash()
    {
        var sourceInstance = CreateInstance();
        var targetInstance = CreateInstance();
        var table = new PoolDemuxBindingTable();
        var consumerId = Guid.NewGuid();

        table.Bind(sourceInstance, "oc-session-1", consumerId, "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 7);

        await sourceInstance.ReportCrashAsync(new InvalidOperationException("process crashed"));
        table.TryGetBinding(sourceInstance, "/repo/one", "oc-session-1", out _).ShouldBeFalse();

        table.MoveBindings(
            sourceInstance,
            targetInstance,
            consumerId,
            "/repo/one",
            sourceLeaseGeneration: 7,
            targetLeaseGeneration: 8);

        table.TryResolveConsumer(targetInstance, "/repo/one", "oc-session-1", out var resolvedConsumerId, out var leaseGeneration)
            .ShouldBeTrue();
        resolvedConsumerId.ShouldBe(consumerId);
        leaseGeneration.ShouldBe(8);
        table.TryVerifyCommandBinding(targetInstance, "fleet-session-1", "user-1", "oc-session-1", "/repo/one", 8, out _)
            .ShouldBeTrue();
        table.TryVerifyCommandBinding(targetInstance, "fleet-session-1", "user-1", "oc-session-1", "/repo/one", 7, out _)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task unavailable_instance_bindings_do_not_resolve_and_unconditional_remove_clears_them()
    {
        var instance = CreateInstance();
        var table = new PoolDemuxBindingTable();
        table.Bind(instance, "oc-session-1", Guid.NewGuid(), "fleet-session-1", "user-1", "/repo/one", leaseGeneration: 1);

        await instance.DisposeAsync();

        table.TryGetBinding(instance, "/repo/one", "oc-session-1", out _).ShouldBeFalse();
        table.TryResolveConsumer(instance, "/repo/one", "oc-session-1", out var consumerId, out var leaseGeneration).ShouldBeFalse();
        consumerId.ShouldBe(Guid.Empty);
        leaseGeneration.ShouldBe(0);
        table.Remove(instance, "oc-session-1").ShouldBeTrue();
        table.Remove(instance, "oc-session-1").ShouldBeFalse();
    }

    [Fact]
    public void invalid_arguments_are_rejected()
    {
        var instance = CreateInstance();
        var table = new PoolDemuxBindingTable();

        Should.Throw<ArgumentNullException>(() => table.Bind(null!, "oc-session-1", Guid.NewGuid(), "fleet-session-1", "user-1", "/repo/one", 1));
        Should.Throw<ArgumentException>(() => table.Bind(instance, " ", Guid.NewGuid(), "fleet-session-1", "user-1", "/repo/one", 1));
        Should.Throw<ArgumentException>(() => table.Bind(instance, "oc-session-1", Guid.NewGuid(), " ", "user-1", "/repo/one", 1));
        Should.Throw<ArgumentException>(() => table.Bind(instance, "oc-session-1", Guid.NewGuid(), "fleet-session-1", " ", "/repo/one", 1));
        Should.Throw<ArgumentException>(() => table.Bind(instance, "oc-session-1", Guid.NewGuid(), "fleet-session-1", "user-1", " ", 1));
        Should.Throw<ArgumentOutOfRangeException>(() => table.Bind(instance, "oc-session-1", Guid.NewGuid(), "fleet-session-1", "user-1", "/repo/one", -1));
        Should.Throw<ArgumentOutOfRangeException>(() => table.TryGetBinding(instance, "/repo/one", "oc-session-1", -1, out _));
        Should.Throw<ArgumentOutOfRangeException>(() => table.TryVerifyCommandBinding(instance, "fleet-session-1", "user-1", "oc-session-1", "/repo/one", -1, out _));
        Should.Throw<ArgumentOutOfRangeException>(() => table.Remove(instance, "oc-session-1", -1));
        Should.Throw<ArgumentNullException>(() => table.RemoveForLease(null!, Guid.NewGuid(), "fleet-session-1", "/repo/one", 1));
        Should.Throw<ArgumentException>(() => table.RemoveForLease(instance, Guid.NewGuid(), " ", "/repo/one", 1));
        Should.Throw<ArgumentException>(() => table.RemoveForLease(instance, Guid.NewGuid(), "fleet-session-1", " ", 1));
        Should.Throw<ArgumentOutOfRangeException>(() => table.RemoveForLease(instance, Guid.NewGuid(), "fleet-session-1", "/repo/one", -1));
        Should.Throw<ArgumentNullException>(() => table.MoveBindings(null!, instance, Guid.NewGuid(), "/repo/one", 1, 2));
        Should.Throw<ArgumentNullException>(() => table.MoveBindings(instance, null!, Guid.NewGuid(), "/repo/one", 1, 2));
        Should.Throw<ArgumentException>(() => table.MoveBindings(instance, instance, Guid.NewGuid(), " ", 1, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => table.MoveBindings(instance, instance, Guid.NewGuid(), "/repo/one", -1, 2));
        Should.Throw<ArgumentOutOfRangeException>(() => table.MoveBindings(instance, instance, Guid.NewGuid(), "/repo/one", 1, -1));
    }

    private static PooledOpenCodeInstance CreateInstance()
    {
        return new PooledOpenCodeInstance(
            "key",
            $"instance-{Guid.NewGuid():N}",
            processId: 123,
            shutdownAsync: () => ValueTask.CompletedTask);
    }

    private static OpenCodeSseEvent CreateEvent(string type, string sessionId)
    {
        var properties = JsonSerializer.SerializeToElement(new { sessionID = sessionId });
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

    private sealed class FakeStreamFactory : IOpenCodeSseEventStreamFactory
    {
        private readonly object _sync = new();
        private readonly Dictionary<StreamKey, List<FakeStream>> _streams = new();

        public async IAsyncEnumerable<OpenCodeSseEvent> SubscribeAsync(
            PooledOpenCodeInstance instance,
            string directory,
            Func<Task> connectedAsync,
            [EnumeratorCancellation] CancellationToken ct)
        {
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

            await connectedAsync().ConfigureAwait(false);

            await using var cancellation = ct.Register(stream.Cancel);
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

        private readonly record struct StreamKey(PooledOpenCodeInstance Instance, string Directory);
    }

    private sealed class FakeStream
    {
        private readonly Channel<OpenCodeSseEvent> _events = Channel.CreateUnbounded<OpenCodeSseEvent>();
        private int _canceledFlag;

        public ValueTask WriteAsync(OpenCodeSseEvent evt) => _events.Writer.WriteAsync(evt);

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _canceledFlag, 1) == 0)
            {
                _events.Writer.TryComplete();
            }
        }

        public IAsyncEnumerable<OpenCodeSseEvent> ReadAllAsync(CancellationToken ct) => _events.Reader.ReadAllAsync(ct);
    }
}
