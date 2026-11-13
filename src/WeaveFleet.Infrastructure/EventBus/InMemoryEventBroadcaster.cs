using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// In-memory fan-out event broadcaster using System.Threading.Channels.
/// One channel per subscriber; topics are filtered on delivery.
/// Events are scoped to the owning user when a <c>userId</c> is provided —
/// only subscribers with the matching <c>subscriberUserId</c> receive those events.
/// </summary>
public sealed class InMemoryEventBroadcaster : IEventBroadcaster, IDisposable
{
    private const string ActivityTopic = "activity";
    private const string SessionsTopic = "sessions";

    private sealed record Subscription(
        string SubscriberId,
        IReadOnlyList<string> Topics,
        /// <summary>Only events matching this userId (or system events with null userId) are delivered.</summary>
        string? SubscriberUserId,
        Channel<BroadcastEvent> Channel);

    /// <summary>
    /// Most events one subscriber may have waiting. A subscriber that reads more slowly than events arrive (a stalled
    /// socket, a phone on a bad link) would otherwise grow the queue, and the server's memory, for as long as it stays
    /// connected. Past this the subscriber is cut off with <see cref="SubscriberFellBehindException"/>; the hub then
    /// closes that connection and the client reconnects and reloads a snapshot.
    /// </summary>
    internal const int DefaultBacklogLimit = 20_000;

    private readonly int _backlogLimit;
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();

    public InMemoryEventBroadcaster() : this(DefaultBacklogLimit)
    {
    }

    internal InMemoryEventBroadcaster(int backlogLimit) => _backlogLimit = backlogLimit;

    /// <summary>Number of active subscribers (exposed for test synchronisation).</summary>
    internal int SubscriberCount => _subscriptions.Count;

    /// <inheritdoc />
    public Task BroadcastAsync(string topic, string type, JsonElement payload)
        => BroadcastAsync(topic, type, payload, eventId: null, domainEvent: null, userId: null, CancellationToken.None);

    public Task BroadcastAsync(string topic, string type, JsonElement payload, CancellationToken ct)
        => BroadcastAsync(topic, type, payload, eventId: null, domainEvent: null, userId: null, ct);

    public Task BroadcastAsync(
        string topic,
        string type,
        JsonElement payload,
        string? userId,
        CancellationToken ct)
        => BroadcastAsync(topic, type, payload, eventId: null, domainEvent: null, userId, ct);

    public Task BroadcastAsync(
        string topic,
        string type,
        JsonElement payload,
        DomainEvent? domainEvent,
        string? userId,
        CancellationToken ct)
        => BroadcastAsync(topic, type, payload, eventId: null, domainEvent, userId, ct);

    public Task BroadcastAsync(
        string topic,
        string type,
        JsonElement payload,
        long? eventId,
        string? userId,
        CancellationToken ct)
        => BroadcastAsync(topic, type, payload, eventId, domainEvent: null, userId, ct);

    public Task BroadcastAsync(
        string topic,
        string type,
        JsonElement payload,
        long? eventId,
        DomainEvent? domainEvent,
        string? userId,
        CancellationToken ct)
    {
        var evt = new BroadcastEvent(topic, type, payload, DateTimeOffset.UtcNow, eventId, userId)
        {
            DomainEvent = domainEvent
        };

        foreach (var sub in _subscriptions.Values)
        {
            // Topic filter
            if (!MatchesTopic(sub.Topics, topic))
                continue;

            // User-scope filter: deliver only if
            //   • the event has no owner (system event), OR
            //   • the subscriber has no user filter (system subscriber), OR
            //   • the subscriber's userId matches the event's userId
            if (userId is not null
                && sub.SubscriberUserId is not null
                && !string.Equals(userId, sub.SubscriberUserId, StringComparison.Ordinal))
            {
                continue;
            }

            // A full queue means the subscriber stopped reading: cut it off instead of holding events for it forever.
            // Events still queued are delivered first, then the reader sees the exception.
            if (!sub.Channel.Writer.TryWrite(evt))
                sub.Channel.Writer.TryComplete(new SubscriberFellBehindException(_backlogLimit));
        }

        return Task.CompletedTask;
    }

    private static bool MatchesTopic(IReadOnlyList<string> topics, string topic)
    {
        if (topics.Contains("*"))
            return true;

        if (topics.Contains(topic))
            return true;

        return topic == SessionsTopic && topics.Contains(ActivityTopic);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BroadcastEvent> SubscribeAsync(
        IReadOnlyList<string> topics,
        string? subscriberUserId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var channel = Channel.CreateBounded<BroadcastEvent>(new BoundedChannelOptions(_backlogLimit)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        var sub = new Subscription(id, topics, subscriberUserId, channel);
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

/// <summary>A subscriber let more events pile up than the broadcaster keeps for one subscriber.</summary>
public sealed class SubscriberFellBehindException(int limit)
    : Exception($"The subscriber had more than {limit} events waiting and was cut off.");
