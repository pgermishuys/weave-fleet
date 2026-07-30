namespace WeaveFleet.Application.Events;

/// <summary>
/// Provides read access to the in-process event store for snapshot building.
/// </summary>
public interface IEventStore
{
    /// <summary>
    /// Returns the highest event ID for the given session, or 0 if no events exist.
    /// Used to set the dedup watermark when merging snapshots.
    /// </summary>
    long GetLastEventId(string sessionId);
}
