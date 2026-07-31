namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Repository for tracking processed automation events to prevent duplicate execution.
/// </summary>
public interface IAutomationEventLedgerRepository
{
    /// <summary>
    /// Checks if an event has already been processed by an automation.
    /// </summary>
    /// <param name="automationId">The automation identifier.</param>
    /// <param name="sourceEventId">The source event identifier.</param>
    /// <returns>True if the event has been processed; otherwise, false.</returns>
    Task<bool> IsProcessedAsync(string automationId, string sourceEventId);

    /// <summary>
    /// Records that an event has been processed by an automation.
    /// Duplicate inserts are silently ignored.
    /// </summary>
    /// <param name="automationId">The automation identifier.</param>
    /// <param name="sourceEventId">The source event identifier.</param>
    Task RecordAsync(string automationId, string sourceEventId);
}
