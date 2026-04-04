namespace WeaveFleet.Application.Analytics;

/// <summary>
/// Fire-and-forget interface for accepting analytics events into the pipeline.
/// Implementations must never block or throw — they write to a bounded channel internally.
/// </summary>
public interface IAnalyticsCollector
{
    /// <summary>Accepts a token event. Non-blocking, fire-and-forget.</summary>
    void AcceptTokenEvent(TokenEventData data);

    /// <summary>Accepts a session snapshot. Non-blocking, fire-and-forget.</summary>
    void AcceptSessionSnapshot(SessionSnapshotData data);
}
