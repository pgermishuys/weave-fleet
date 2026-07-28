using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

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
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var parentSessionId = tracker.GetParentSessionId(childSessionId);
        if (parentSessionId is null)
            return;

        var parentActivityStatus = tracker.GetEffectiveActivityStatus(parentSessionId);
        if (parentActivityStatus is null)
            return;

        var payload = await BuildActivityStatusPayloadAsync(
            parentSessionId,
            parentActivityStatus,
            scopeFactory,
            ct).ConfigureAwait(false);

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

    private static async Task<JsonElement> BuildActivityStatusPayloadAsync(
        string sessionId,
        string activityStatus,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sessionRepository = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var capabilitiesResolver = scope.ServiceProvider.GetRequiredService<SessionCapabilitiesResolver>();
        var session = await sessionRepository.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is not null)
        {
            session.ActivityStatus = activityStatus;
        }

        ct.ThrowIfCancellationRequested();

        var capabilities = session is not null
            ? capabilitiesResolver.Resolve(session)
            : SessionCapabilitiesResolver.Resolve(null, null, null, activityStatus, isLive: false);

        return InfrastructureJsonContext.SerializeActivityStatus(sessionId, activityStatus, capabilities);
    }
}
