using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Session completion callbacks (<c>onComplete</c> on <c>POST /api/sessions</c>): when the source session finishes,
/// the target session is prompted that it did. <see cref="SessionCallbackDispatcher"/> calls it as the relay sees
/// sessions work and go idle, and every <see cref="SessionCallbackPoller.Interval"/>.
/// </summary>
/// <remarks>
/// <para>
/// A callback is consumed only once its prompt was delivered. Until then it stays in the database and the next idle
/// event or poll tries again, so a Fleet restart, a target that's mid-turn or a prompt that fails loses nothing.
/// </para>
/// <para>
/// The prompt goes to the target <em>session</em> the way any prompt does: to its live instance, or, when it has
/// none, by resuming it. The instance id stored with the callback isn't used: instance ids change when Fleet restarts
/// or the session is resumed, and the session always knows its current one.
/// </para>
/// </remarks>
public sealed partial class SessionCallbackService(
    ISessionCallbackRepository callbackRepository,
    ISessionRepository sessionRepository,
    SessionOrchestrator orchestrator,
    SessionActivityTracker activity,
    ILogger<SessionCallbackService> logger)
{
    /// <summary>How long one delivery may take, resuming the target included, before it's left for the next try.</summary>
    internal static readonly TimeSpan DeliveryTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The source session's agent has started working: its callbacks fire once it's no longer in a turn.</summary>
    public Task<int> MarkSourceStartedAsync(string sourceSessionId)
        => callbackRepository.MarkSourceStartedAsync(sourceSessionId);

    /// <summary>
    /// A session's turn ended. Fires the callbacks it was the source of, and those waiting for it as their target to
    /// be free. Its idle event is the signal, so the activity tracker isn't asked about it: it may not have caught up.
    /// </summary>
    public Task<int> OnSessionIdledAsync(string sessionId, CancellationToken ct = default)
        => FireReadyAsync(sessionId, ct);

    /// <summary>
    /// The poll: fires every started callback whose source isn't in a turn. That covers a source whose turn ended
    /// while Fleet was down (a restart ends every turn, and no idle event comes for it), a target that was busy, and a
    /// delivery that failed before.
    /// </summary>
    public Task<int> ProcessPendingCallbacksAsync(CancellationToken ct = default)
        => FireReadyAsync(idledSessionId: null, ct);

    private async Task<int> FireReadyAsync(string? idledSessionId, CancellationToken ct)
    {
        var started = await callbackRepository.GetStartedAsync().ConfigureAwait(false);
        var fired = 0;
        foreach (var cb in started)
        {
            ct.ThrowIfCancellationRequested();

            if (cb.SourceSessionId != idledSessionId && InTurn(activity.GetEffectiveActivityStatus(cb.SourceSessionId)))
                continue;

            // User-scoped: a source that isn't this user's comes back null.
            var source = await sessionRepository.GetByIdAsync(cb.SourceSessionId).ConfigureAwait(false);
            if (source is null)
                continue;

            // Ownership guard: the target must be the source owner's session. The repository is user-scoped, so a
            // target that's missing or someone else's comes back null.
            var target = await sessionRepository.GetByIdAsync(cb.TargetSessionId).ConfigureAwait(false);
            if (target is null || !string.Equals(target.UserId, source.UserId, StringComparison.Ordinal))
            {
                LogOwnershipGuardRejected(cb.Id, cb.TargetSessionId);
                continue;
            }

            // An archived session can't be prompted; it hears once it's restored.
            if (string.Equals(target.RetentionStatus, "archived", StringComparison.Ordinal))
                continue;

            // A prompt to a busy session goes into the turn it's running, which would change what it's doing. It
            // hears when that turn ends.
            if (cb.TargetSessionId != idledSessionId && InTurn(activity.Get(cb.TargetSessionId)?.ActivityStatus))
                continue;

            if (await DeliverAsync(cb, source, ct).ConfigureAwait(false))
                fired++;
        }

        return fired;
    }

    private async Task<bool> DeliverAsync(SessionCallback cb, Session source, CancellationToken ct)
    {
        var text = $"Session '{source.Title}' ({source.Id}) completed.";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DeliveryTimeout);
        try
        {
            var sent = await orchestrator.PromptSessionOnceAsync(cb.TargetSessionId, text, options: null, timeout.Token).ConfigureAwait(false);
            if (sent.IsFailure)
            {
                LogDeliveryFailed(cb.Id, cb.TargetSessionId, sent.Error.Description);
                return false;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogCallbackFailed(ex, cb.Id);
            return false;
        }

        await callbackRepository.MarkFiredAsync(cb.Id).ConfigureAwait(false);
        LogCallbackFired(cb.Id, cb.TargetSessionId);
        return true;
    }

    private static bool InTurn(string? activityStatus) => SessionActivityTracker.IsInTurn(activityStatus);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Callback {CallbackId}: target session {TargetSessionId} ownership guard rejected (cross-user reference).")]
    private partial void LogOwnershipGuardRejected(string callbackId, string targetSessionId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Callback {CallbackId} fired → target session {TargetSessionId}.")]
    private partial void LogCallbackFired(string callbackId, string targetSessionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Callback {CallbackId} wasn't delivered to session {TargetSessionId}; it stays pending: {Reason}")]
    private partial void LogDeliveryFailed(string callbackId, string targetSessionId, string reason);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Callback {CallbackId} failed to send prompt; it stays pending.")]
    private partial void LogCallbackFailed(Exception ex, string callbackId);
}
