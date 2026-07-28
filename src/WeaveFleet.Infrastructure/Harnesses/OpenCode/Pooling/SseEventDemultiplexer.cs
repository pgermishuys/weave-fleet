using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Diagnostics;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

internal interface IOpenCodeSseEventBindingResolver
{
    bool TryResolveConsumer(
        PooledOpenCodeInstance instance,
        string directory,
        string openCodeSessionId,
        out Guid consumerId,
        out long leaseGeneration);
}

internal interface IOpenCodeSseEventStreamFactory
{
    IAsyncEnumerable<OpenCodeSseEvent> SubscribeAsync(
        PooledOpenCodeInstance instance,
        string directory,
        Func<Task> connectedAsync,
        CancellationToken ct);
}

/// <summary>
/// Shares one OpenCode SSE stream per active pooled instance and directory, then routes
/// attributable events to the registered Fleet session consumer channel.
/// </summary>
internal sealed class SseEventDemultiplexer : IAsyncDisposable
{
    private const string MissingSessionIdReason = "missing_session_id";
    private const string UnboundSessionIdReason = "unbound_session_id";
    private const string UnregisteredConsumerReason = "unregistered_consumer";

    private static readonly Counter<long> DroppedUnattributableEvents = FleetInstrumentation.Meter.CreateCounter<long>(
        "weave_fleet.opencode.sse.unattributable_events.dropped",
        "events",
        "OpenCode SSE events dropped because they could not be safely attributed to an active Fleet session consumer.");

    private static readonly Action<ILogger, string, string, Exception?> LogDroppedUnattributableEvent =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, "DroppedUnattributableEvent"),
            "Dropped unattributable OpenCode SSE event of type {EventType}; reason: {Reason}.");

    private static readonly Action<ILogger, string, string, Exception?> LogStreamRestart =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2, "StreamRestart"),
            "OpenCode SSE directory stream for instance {InstanceId} and directory {Directory} stopped; reconnecting.");

    private static readonly Action<ILogger, string, string, Exception?> LogStreamFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(3, "StreamFailed"),
            "OpenCode SSE directory stream for instance {InstanceId} and directory {Directory} failed; reconnecting.");

    private readonly ConcurrentDictionary<DirectoryStreamKey, DirectoryStreamEntry> _streams = new();
    private readonly IOpenCodeSseEventBindingResolver _bindingResolver;
    private readonly IOpenCodeSseEventStreamFactory _streamFactory;
    private readonly ILogger<SseEventDemultiplexer> _logger;
    private readonly TimeSpan _initialReconnectDelay;
    private readonly TimeSpan _maxReconnectDelay;
    private long _droppedUnattributableEventCount;
    private int _disposed;

    public SseEventDemultiplexer(
        IOpenCodeSseEventBindingResolver bindingResolver,
        ILogger<SseEventDemultiplexer> logger)
        : this(
            bindingResolver,
            new OpenCodeHttpClientSseEventStreamFactory(),
            logger,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30))
    {
    }

    internal SseEventDemultiplexer(
        IOpenCodeSseEventBindingResolver bindingResolver,
        IOpenCodeSseEventStreamFactory streamFactory,
        ILogger<SseEventDemultiplexer> logger,
        TimeSpan initialReconnectDelay,
        TimeSpan maxReconnectDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialReconnectDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReconnectDelay, initialReconnectDelay);

        _bindingResolver = bindingResolver;
        _streamFactory = streamFactory;
        _logger = logger;
        _initialReconnectDelay = initialReconnectDelay;
        _maxReconnectDelay = maxReconnectDelay;
    }

    internal long DroppedUnattributableEventCount => Volatile.Read(ref _droppedUnattributableEventCount);

    internal async Task WaitForActiveStreamAsync(PooledOpenCodeInstance instance, string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (!_streams.TryGetValue(new DirectoryStreamKey(instance, directory), out var entry))
        {
            throw new InvalidOperationException("No OpenCode SSE directory stream has been registered for the pooled session.");
        }

        await entry.WaitForActiveStreamAsync().WaitAsync(ct).ConfigureAwait(false);
    }

    public ValueTask<IAsyncDisposable> RegisterConsumerAsync(
        PooledOpenCodeInstance instance,
        string directory,
        Guid consumerId,
        Channel<OpenCodeSseEvent> consumerChannel,
        CancellationToken ct)
    {
        return RegisterConsumerAsync(instance, directory, consumerId, 0, consumerChannel, ct);
    }

    public async ValueTask<IAsyncDisposable> RegisterConsumerAsync(
        PooledOpenCodeInstance instance,
        string directory,
        Guid consumerId,
        long leaseGeneration,
        Channel<OpenCodeSseEvent> consumerChannel,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (leaseGeneration < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseGeneration), leaseGeneration, "Lease generation must be non-negative.");
        }

        ArgumentNullException.ThrowIfNull(consumerChannel);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        DirectoryStreamKey key;
        DirectoryStreamEntry entry;
        while (true)
        {
            key = new DirectoryStreamKey(instance, directory);
            entry = _streams.GetOrAdd(key, CreateEntry);
            lock (entry.Sync)
            {
                if (entry.Stopping || !_streams.TryGetValue(key, out var currentEntry) || !ReferenceEquals(entry, currentEntry))
                {
                    continue;
                }

                if (!entry.Consumers.TryAdd(consumerId, new ConsumerEntry(consumerChannel, leaseGeneration)))
                {
                    throw new InvalidOperationException("An OpenCode SSE consumer is already registered with the same identifier.");
                }

                break;
            }
        }

        try
        {
            await entry.WaitForActiveStreamAsync().WaitAsync(ct).ConfigureAwait(false);
            return new ConsumerRegistration(this, key, consumerId);
        }
        catch
        {
            UnregisterConsumer(key, consumerId);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var entries = _streams.ToArray();
        _streams.Clear();

        foreach (var pair in entries)
        {
            StopEntry(pair.Value);
        }

        foreach (var pair in entries)
        {
            await pair.Value.Pump.ConfigureAwait(false);
        }
    }

    private DirectoryStreamEntry CreateEntry(DirectoryStreamKey key)
    {
        var entry = new DirectoryStreamEntry(key);
        entry.Pump = Task.Run(() => PumpDirectoryAsync(entry), CancellationToken.None);
        return entry;
    }

    private async Task PumpDirectoryAsync(DirectoryStreamEntry entry)
    {
        var delay = _initialReconnectDelay;

        try
        {
            while (!entry.Cancellation.IsCancellationRequested)
            {
                entry.MarkStreamInactive();
                try
                {
                    await foreach (var evt in _streamFactory
                                       .SubscribeAsync(
                                           entry.Key.Instance,
                                           entry.Key.Directory,
                                           entry.NotifyActiveStreamAsync,
                                           entry.Cancellation.Token)
                                       .WithCancellation(entry.Cancellation.Token)
                                       .ConfigureAwait(false))
                    {
                        RouteEvent(entry, evt);
                    }

                    if (!entry.Cancellation.IsCancellationRequested)
                    {
                        LogStreamRestart(_logger, entry.Key.Instance.InstanceId, entry.Key.Directory, null);
                    }
                }
                catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (entry.Cancellation.IsCancellationRequested)
                {
                    LogStreamFailed(_logger, entry.Key.Instance.InstanceId, entry.Key.Directory, ex);
                    break;
                }
                catch (Exception ex)
                {
                    LogStreamFailed(_logger, entry.Key.Instance.InstanceId, entry.Key.Directory, ex);
                }

                entry.MarkStreamInactive();

                if (!entry.Cancellation.IsCancellationRequested && delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, entry.Cancellation.Token).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _maxReconnectDelay.TotalMilliseconds));
                }
            }
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            entry.Cancellation.Dispose();
        }
    }

    private void RouteEvent(DirectoryStreamEntry entry, OpenCodeSseEvent evt)
    {
        var openCodeSessionId = OpenCodeMapper.TryResolveSessionId(evt);
        if (string.IsNullOrWhiteSpace(openCodeSessionId))
        {
            RecordDroppedUnattributableEvent(evt, MissingSessionIdReason);
            return;
        }

        if (!_bindingResolver.TryResolveConsumer(
                entry.Key.Instance,
                entry.Key.Directory,
                openCodeSessionId,
                out var consumerId,
                out var leaseGeneration))
        {
            RecordDroppedUnattributableEvent(evt, UnboundSessionIdReason);
            return;
        }

        ConsumerEntry consumerEntry;
        lock (entry.Sync)
        {
            if (!entry.Consumers.TryGetValue(consumerId, out consumerEntry))
            {
                RecordDroppedUnattributableEvent(evt, UnregisteredConsumerReason);
                return;
            }
        }

        if (consumerEntry.LeaseGeneration != leaseGeneration)
        {
            RecordDroppedUnattributableEvent(evt, UnregisteredConsumerReason);
            return;
        }

        if (!consumerEntry.Channel.Writer.TryWrite(evt))
        {
            RecordDroppedUnattributableEvent(evt, UnregisteredConsumerReason);
        }
    }

    private void UnregisterConsumer(DirectoryStreamKey key, Guid consumerId)
    {
        if (!_streams.TryGetValue(key, out var entry))
        {
            return;
        }

        var stop = false;
        lock (entry.Sync)
        {
            entry.Consumers.Remove(consumerId);
            if (entry.Consumers.Count == 0)
            {
                entry.Stopping = true;
                stop = true;
            }
        }

        if (!stop)
        {
            return;
        }

        if (_streams.TryRemove(key, out var removedEntry) && ReferenceEquals(entry, removedEntry))
        {
            StopEntry(entry);
        }
    }

    private static void StopEntry(DirectoryStreamEntry entry)
    {
        lock (entry.Sync)
        {
            entry.Stopping = true;
            entry.Consumers.Clear();
        }

        entry.Cancellation.Cancel();
    }

    private void RecordDroppedUnattributableEvent(OpenCodeSseEvent evt, string reason)
    {
        Interlocked.Increment(ref _droppedUnattributableEventCount);
        DroppedUnattributableEvents.Add(1, new KeyValuePair<string, object?>("reason", reason));
        LogDroppedUnattributableEvent(_logger, evt.Type, reason, null);
    }

    private readonly record struct DirectoryStreamKey(PooledOpenCodeInstance Instance, string Directory);

    private sealed class DirectoryStreamEntry
    {
        public DirectoryStreamEntry(DirectoryStreamKey key)
        {
            Key = key;
        }

        public DirectoryStreamKey Key { get; }

        public object Sync { get; } = new();

        public Dictionary<Guid, ConsumerEntry> Consumers { get; } = new();

        public CancellationTokenSource Cancellation { get; } = new();

        public bool Stopping { get; set; }

        public Task Pump { get; set; } = Task.CompletedTask;

        private TaskCompletionSource ActiveStreamReady { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForActiveStreamAsync()
        {
            lock (Sync)
            {
                return ActiveStreamReady.Task;
            }
        }

        public void MarkStreamInactive()
        {
            lock (Sync)
            {
                if (ActiveStreamReady.Task.IsCompleted)
                {
                    ActiveStreamReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        public Task NotifyActiveStreamAsync()
        {
            lock (Sync)
            {
                ActiveStreamReady.TrySetResult();
            }

            return Task.CompletedTask;
        }

    }

    private readonly record struct ConsumerEntry(Channel<OpenCodeSseEvent> Channel, long LeaseGeneration);

    private sealed class ConsumerRegistration : IAsyncDisposable
    {
        private readonly SseEventDemultiplexer _demultiplexer;
        private readonly DirectoryStreamKey _key;
        private readonly Guid _consumerId;
        private int _disposed;

        public ConsumerRegistration(SseEventDemultiplexer demultiplexer, DirectoryStreamKey key, Guid consumerId)
        {
            _demultiplexer = demultiplexer;
            _key = key;
            _consumerId = consumerId;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _demultiplexer.UnregisterConsumer(_key, _consumerId);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class OpenCodeHttpClientSseEventStreamFactory : IOpenCodeSseEventStreamFactory
    {
        public async IAsyncEnumerable<OpenCodeSseEvent> SubscribeAsync(
            PooledOpenCodeInstance instance,
            string directory,
            Func<Task> connectedAsync,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (instance.HttpClient is null)
            {
                throw new InvalidOperationException("Pooled OpenCode instance does not expose an HTTP client for SSE streaming.");
            }

            await foreach (var evt in instance.HttpClient.SubscribeToEventsAsync(directory, connectedAsync, ct).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
    }
}
