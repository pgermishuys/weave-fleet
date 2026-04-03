namespace WeaveFleet.Application.Services;

/// <summary>
/// In-memory pub/sub service for real-time event broadcasting.
/// WebSocket and SSE endpoints subscribe to receive events pushed from the session lifecycle.
/// </summary>
public interface IEventBroadcaster
{
    /// <summary>
    /// Publish an event to all subscribers of <paramref name="topic"/>.
    /// </summary>
    Task BroadcastAsync(string topic, string type, object payload, CancellationToken ct = default);

    /// <summary>
    /// Subscribe to one or more <paramref name="topics"/>.
    /// Completes when <paramref name="ct"/> is cancelled or the broadcaster is disposed.
    /// </summary>
    IAsyncEnumerable<BroadcastEvent> SubscribeAsync(IReadOnlyList<string> topics, CancellationToken ct);
}
