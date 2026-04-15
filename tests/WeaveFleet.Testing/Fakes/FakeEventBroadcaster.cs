using WeaveFleet.Application.Services;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeEventBroadcaster : IEventBroadcaster
{
    public List<BroadcastRecord> Broadcasts { get; } = [];

    /// <summary>
    /// Optional callback invoked after every broadcast is recorded.
    /// Supports TaskCompletionSource signaling in async tests (e.g., HarnessEventRelayTests).
    /// </summary>
    public Action<string, string, object, string?, CancellationToken>? OnBroadcast { get; set; }

    public Task BroadcastAsync(string topic, string type, object payload)
    {
        Broadcasts.Add(new(topic, type, payload, null, null));
        OnBroadcast?.Invoke(topic, type, payload, null, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(string topic, string type, object payload, CancellationToken ct)
    {
        Broadcasts.Add(new(topic, type, payload, null, null));
        OnBroadcast?.Invoke(topic, type, payload, null, ct);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(string topic, string type, object payload, string? userId, CancellationToken ct)
    {
        Broadcasts.Add(new(topic, type, payload, null, userId));
        OnBroadcast?.Invoke(topic, type, payload, userId, ct);
        return Task.CompletedTask;
    }

    public Task BroadcastAsync(string topic, string type, object payload, long? sequenceNumber, string? userId, CancellationToken ct)
    {
        Broadcasts.Add(new(topic, type, payload, sequenceNumber, userId));
        OnBroadcast?.Invoke(topic, type, payload, userId, ct);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<BroadcastEvent> SubscribeAsync(IReadOnlyList<string> topics, string? subscriberUserId, CancellationToken ct)
        => AsyncEnumerable.Empty<BroadcastEvent>();

    public sealed record BroadcastRecord(string Topic, string Type, object Payload, long? SequenceNumber, string? UserId);
}
