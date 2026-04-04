using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// In-memory fan-out event broadcaster using System.Threading.Channels.
/// One channel per subscriber; topics are filtered on delivery.
/// </summary>
public sealed class InMemoryEventBroadcaster : IEventBroadcaster, IDisposable
{
    private sealed record Subscription(
        string SubscriberId,
        IReadOnlyList<string> Topics,
        Channel<BroadcastEvent> Channel);

    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

    /// <inheritdoc />
    public Task BroadcastAsync(string topic, string type, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToElement(payload);
        var evt = new BroadcastEvent(topic, type, json, DateTimeOffset.UtcNow);

        foreach (var sub in _subscriptions.Values)
        {
            if (!sub.Topics.Contains("*") && !sub.Topics.Contains(topic))
                continue;

            // TryWrite is fine — unbounded channel; drop if disposed
            sub.Channel.Writer.TryWrite(evt);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BroadcastEvent> SubscribeAsync(
        IReadOnlyList<string> topics,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var channel = Channel.CreateUnbounded<BroadcastEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var sub = new Subscription(id, topics, channel);
        _subscriptions[id] = sub;

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }
        }
        finally
        {
            _subscriptions.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        foreach (var sub in _subscriptions.Values)
            sub.Channel.Writer.TryComplete();
        _subscriptions.Clear();
    }
}
