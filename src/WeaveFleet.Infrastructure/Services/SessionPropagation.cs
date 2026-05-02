using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Shared helper for propagating derived busy/idle activity status to parent sessions when a
/// child session's activity state changes. Used by <c>InProcessFanOutService</c>.
/// </summary>
internal static class SessionPropagation
{
    /// <summary>
    /// After a child session's activity status changes, propagates the derived effective
    /// activity status to its registered parent session (if any). Broadcasts on both the
    /// global <c>sessions</c> topic (for list updates) and the per-session topic (for the
    /// detail view).
    /// </summary>
    internal static async Task PropagateToParentAsync(
        string childSessionId,
        string? userId,
        SessionActivityTracker tracker,
        IEventBroadcaster broadcaster,
        CancellationToken ct)
    {
        var parentSessionId = tracker.GetParentSessionId(childSessionId);
        if (parentSessionId is null)
            return;

        var parentActivityStatus = tracker.GetEffectiveActivityStatus(parentSessionId);
        if (parentActivityStatus is null)
            return;

        var payload = InfrastructureJsonContext.SerializeActivityStatus(parentSessionId, parentActivityStatus);

        await broadcaster.BroadcastAsync(
            "sessions",
            "activity_status",
            payload,
            userId,
            ct).ConfigureAwait(false);

        await broadcaster.BroadcastAsync(
            $"session:{parentSessionId}",
            "activity_status",
            payload,
            userId,
            ct).ConfigureAwait(false);
    }
}
