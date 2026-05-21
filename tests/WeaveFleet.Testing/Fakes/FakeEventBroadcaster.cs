using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeEventBroadcaster : IEventBroadcaster
{
    public List<BroadcastRecord> Broadcasts { get; } = [];

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
}
