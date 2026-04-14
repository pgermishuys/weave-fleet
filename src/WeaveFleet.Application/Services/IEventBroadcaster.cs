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
    /// <param name="topic">The pub/sub topic.</param>
    /// <param name="type">The event type name.</param>
    /// <param name="payload">Event payload to serialize.</param>
    /// <param name="userId">
    /// The owner's user ID. When non-null, only subscribers with the same user ID receive the event.
    /// Null means system-level — delivered to all matching subscribers regardless of user.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task BroadcastAsync(string topic, string type, object payload);

    Task BroadcastAsync(string topic, string type, object payload, CancellationToken ct);

    Task BroadcastAsync(string topic, string type, object payload, string? userId, CancellationToken ct);

    Task BroadcastAsync(string topic, string type, object payload, long? sequenceNumber, string? userId, CancellationToken ct);

    /// <summary>
    /// Subscribe to one or more <paramref name="topics"/>.
    /// Completes when <paramref name="ct"/> is cancelled or the broadcaster is disposed.
    /// </summary>
    /// <param name="topics">Topics to subscribe to. Use "*" for all topics.</param>
    /// <param name="subscriberUserId">
    /// When non-null, only events whose <see cref="BroadcastEvent.UserId"/> matches this value
    /// (or is null) are delivered. When null, all events are delivered (system/internal use only).
    /// Track 3 will extend delivery to session participants.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<BroadcastEvent> SubscribeAsync(IReadOnlyList<string> topics, string? subscriberUserId, CancellationToken ct);
}
