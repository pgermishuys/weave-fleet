using System.Text.Json;
using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeEventBroadcaster : IEventBroadcaster
{
    // Relay pumps and the fan-out service broadcast from different threads; a plain List loses entries when two Adds race.
    public BroadcastLog Broadcasts { get; } = new();

    /// <summary>
    /// Optional callback invoked after every broadcast is recorded.
    /// Supports TaskCompletionSource signaling in async tests (e.g., HarnessEventRelayTests).
    /// </summary>
    public Action<string, string, JsonElement, string?, CancellationToken>? OnBroadcast { get; set; }

    public Task BroadcastAsync(string topic, string type, JsonElement payload, string? userId, CancellationToken ct)
    {
        Broadcasts.Add(new(topic, type, payload, null, userId, null));
        OnBroadcast?.Invoke(topic, type, payload, userId, ct);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(string topic, string type, JsonElement payload, DomainEvent? domainEvent, string? userId, CancellationToken ct)
    {
        Broadcasts.Add(new(topic, type, payload, null, userId, domainEvent));
        OnBroadcast?.Invoke(topic, type, payload, userId, ct);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(string topic, string type, JsonElement payload, long? eventId, string? userId, CancellationToken ct)
    {
        Broadcasts.Add(new(topic, type, payload, eventId, userId, null));
        OnBroadcast?.Invoke(topic, type, payload, userId, ct);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(string topic, string type, JsonElement payload, long? eventId, DomainEvent? domainEvent, string? userId, CancellationToken ct)
    {
        Broadcasts.Add(new(topic, type, payload, eventId, userId, domainEvent));
        OnBroadcast?.Invoke(topic, type, payload, userId, ct);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<BroadcastEvent> SubscribeAsync(IReadOnlyList<string> topics, string? subscriberUserId, CancellationToken ct)
        => AsyncEnumerable.Empty<BroadcastEvent>();

    public sealed record BroadcastRecord(string Topic, string Type, JsonElement Payload, long? EventId, string? UserId, DomainEvent? DomainEvent)
    {
        /// <summary>Deprecated compatibility alias for <see cref="EventId"/>.</summary>
        public long? SequenceNumber => EventId;
    }

    /// <summary>A thread-safe list of what was broadcast. Reads see a snapshot, so they can run while broadcasts arrive.</summary>
    public sealed class BroadcastLog : IEnumerable<BroadcastRecord>
    {
        private readonly object _gate = new();
        private readonly List<BroadcastRecord> _items = [];

        public int Count { get { lock (_gate) return _items.Count; } }

        public BroadcastRecord this[int index] { get { lock (_gate) return _items[index]; } }

        public void Add(BroadcastRecord record) { lock (_gate) _items.Add(record); }

        public void Clear() { lock (_gate) _items.Clear(); }

        public IEnumerator<BroadcastRecord> GetEnumerator()
        {
            BroadcastRecord[] snapshot;
            lock (_gate) snapshot = [.. _items];
            return ((IEnumerable<BroadcastRecord>)snapshot).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
