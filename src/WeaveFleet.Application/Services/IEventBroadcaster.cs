using System.Text.Json;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Services;

/// <summary>
/// In-memory pub/sub service for real-time event broadcasting.
/// WebSocket and SSE endpoints subscribe to receive events pushed from the session lifecycle.
/// </summary>
public interface IEventBroadcaster
{
    Task BroadcastAsync(string topic, string type, JsonElement payload, string? userId, CancellationToken ct);

    Task BroadcastAsync(string topic, string type, JsonElement payload, long? eventId, string? userId, CancellationToken ct);

    /// <summary>
    /// Broadcasts a raw event together with an optional translated domain event.
    /// </summary>
    Task BroadcastAsync(string topic, string type, JsonElement payload, DomainEvent? domainEvent, string? userId, CancellationToken ct);

    /// <summary>
    /// Broadcasts a raw event together with an optional translated domain event and durable event ID.
    /// </summary>
    Task BroadcastAsync(string topic, string type, JsonElement payload, long? eventId, DomainEvent? domainEvent, string? userId, CancellationToken ct);

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
