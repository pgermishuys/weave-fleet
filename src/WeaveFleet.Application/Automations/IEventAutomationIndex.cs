namespace WeaveFleet.Application.Automations;

/// <summary>
/// In-memory view of which event types the enabled event-trigger automations listen for, so the
/// event pipeline doesn't query the database for every published event.
/// </summary>
public interface IEventAutomationIndex
{
    /// <summary>Forget what was loaded; call whenever an automation is created, updated, enabled, disabled or deleted.</summary>
    void Invalidate();

    /// <summary>
    /// A synchronous, allocation-free pre-check for the publisher. False only when the index is loaded and no
    /// enabled event automation listens for this event type; true when unsure (not loaded yet).
    /// </summary>
    bool MayMatch(string eventType);

    /// <summary>Loads the index if needed, then says whether any enabled event automation listens for this event type.</summary>
    Task<bool> AnyListensToAsync(string eventType, CancellationToken ct);
}
