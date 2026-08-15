namespace WeaveFleet.Application.Services;

/// <summary>
/// Notifies the automation event dispatcher of domain events that may trigger automations.
/// </summary>
public interface IAutomationEventNotifier
{
    /// <summary>
    /// Notifies the automation dispatcher of a domain event.
    /// </summary>
    /// <param name="eventType">The domain event type (e.g., "session.started", "message.created").</param>
    /// <param name="eventId">The unique event identifier for deduplication.</param>
    /// <param name="sessionId">The session ID that emitted the event, if any.</param>
    /// <param name="sessionSourceReference">The source_reference of the session that emitted the event, if any.</param>
    /// <param name="eventSummary">Optional human-readable summary of the event for context.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyAsync(
        string eventType,
        string eventId,
        string? sessionId,
        string? sessionSourceReference,
        string? eventSummary,
        CancellationToken ct);
}
