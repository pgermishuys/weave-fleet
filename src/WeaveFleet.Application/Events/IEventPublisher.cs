using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Events;

/// <summary>
/// Publishes <see cref="HarnessEvent"/>s to the event substrate. Implementations route durable
/// and ephemeral events appropriately.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publish a single event. For durable events, completes once the broker has acknowledged
    /// receipt; for ephemeral events, completes when the publish has been handed to the client
    /// library. Caller-side ordering is preserved when called serially.
    /// </summary>
    Task<PublishResult> PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct);
}

/// <summary>
/// Result from appending/publishing an event.
/// </summary>
public readonly record struct PublishResult(long? EventId, bool IsDuplicate);
