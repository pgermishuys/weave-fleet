using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Compact now: the harness summarises the session's conversation so the agent can carry on in a smaller context.
/// Gated as a prompt is (the caller's own session, not archived), and only between turns.
/// </summary>
public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// Asks the session's harness to compact its context. Returns once the harness has taken the request; the
    /// compaction's progress and end reach clients as <c>context.updated</c>.
    /// </summary>
    public async Task<Result<Unit>> CompactAsync(string id, CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(id);
        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var session = sessionResult.Value;
        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");

        if (SessionCapabilitiesResolver.CompactUnsupportedReason(harnessRegistry.GetByType(session.HarnessType)) is { } unsupported)
            return FleetError.ValidationError("Session.Compact", unsupported);

        if (SessionActivityTracker.IsInTurn(sessionActivityTracker.GetEffectiveActivityStatus(id)))
            return new FleetError("General.Conflict", "Wait for the agent to finish its turn, then compact.");

        var instanceResult = await GetOrActivateInstanceAsync(session, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        try
        {
            // The compaction's end arrives as events, so the subscription has to be up before it starts.
            await EnsureEventSubscriptionReadyAsync(instanceResult.Value, id, ct).ConfigureAwait(false);

            var choices = WithSessionChoices(options: null, session);
            await instanceResult.Value.CompactAsync(
                new CompactOptions { ProviderId = choices?.ProviderId, ModelId = choices?.ModelId },
                ct).ConfigureAwait(false);
            return Unit.Value;
        }
        catch (HarnessBusyException ex)
        {
            return new FleetError("General.Conflict", ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return FleetError.ValidationError("Session.Compact", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCompactFailed(ex, id);
            return new FleetError("Session.CompactFailed", "The context couldn't be compacted. Fleet's log has the details.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to compact the context of session {SessionId}")]
    private partial void LogCompactFailed(Exception ex, string sessionId);
}
