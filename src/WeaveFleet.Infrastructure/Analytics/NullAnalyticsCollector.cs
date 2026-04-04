using WeaveFleet.Application.Analytics;

namespace WeaveFleet.Infrastructure.Analytics;

/// <summary>
/// No-op implementation of <see cref="IAnalyticsCollector"/> used when analytics is disabled.
/// All methods are empty — zero allocations, no side effects.
/// </summary>
internal sealed class NullAnalyticsCollector : IAnalyticsCollector
{
    /// <inheritdoc />
    public void AcceptTokenEvent(TokenEventData data) { }

    /// <inheritdoc />
    public void AcceptSessionSnapshot(SessionSnapshotData data) { }
}
