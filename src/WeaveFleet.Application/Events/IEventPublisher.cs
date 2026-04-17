using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Events;

/// <summary>
/// Publishes <see cref="HarnessEvent"/>s to the event substrate. Implementations route durable
/// and ephemeral events to the appropriate transport (JetStream vs core NATS today).
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Publish a single event. For durable events, completes once the broker has acknowledged
    /// receipt; for ephemeral events, completes when the publish has been handed to the client
    /// library. Caller-side ordering is preserved when called serially.
    /// </summary>
    Task PublishAsync(HarnessEvent evt, EventPublishContext context, CancellationToken ct);
}
