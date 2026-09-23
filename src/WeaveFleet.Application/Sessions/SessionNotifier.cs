using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>Whether a user has turned desktop notifications on. Off unless they have.</summary>
public interface INotificationPreference
{
    Task<bool> IsEnabledAsync(string userId, CancellationToken ct);
}

/// <summary>
/// Tells you about a session while you're looking somewhere else. Two moments are worth interrupting for:
/// the agent stopped on a question only you can answer, and a turn ended. Whether you're looking is the
/// same signal the session recap waits on — a tab that has the session open, visible and focused — so
/// Fleet is quiet about the session in front of you and speaks up about the ones behind it.
/// The notification is pushed to the global sessions topic; the browser shows it.
/// </summary>
public sealed partial class SessionNotifier(
    SessionFocusTracker focusTracker,
    IEventBroadcaster eventBroadcaster,
    INotificationPreference preference,
    IServiceScopeFactory scopeFactory,
    ILogger<SessionNotifier> logger)
{
    /// <summary>The event type on the global sessions topic.</summary>
    public const string EventType = "session_notification";

    // session id → the last activity status we were told about
    private readonly ConcurrentDictionary<string, string> _last = new(StringComparer.Ordinal);

    /// <summary>Called for every activity change the harness reports, on the relay's pump.</summary>
    public void OnActivityChanged(string sessionId, string activityStatus)
    {
        var previous = _last.TryGetValue(sessionId, out var last) ? last : null;
        _last[sessionId] = activityStatus;

        var reason = Reason(previous, activityStatus);
        if (reason is null)
            return;

        // Whoever is looking at the session can see this happen; they don't need telling.
        if (focusTracker.IsWatched(sessionId))
            return;

        _ = NotifyAsync(sessionId, reason);
    }

    /// <summary>
    /// A workflow run stopped on the user, with its card in this session: a You step, or a step that stopped without
    /// an outcome. Waiting on the user is the run's state, not the session's, so it doesn't come as an activity change.
    /// </summary>
    public void OnWorkflowNeedsYou(string sessionId, string body)
    {
        if (focusTracker.IsWatched(sessionId))
            return;

        _ = NotifyAsync(sessionId, SessionNotificationReasons.NeedsYou, body);
    }

    /// <summary>Drops what's held for a deleted session.</summary>
    public void Forget(string sessionId) => _last.TryRemove(sessionId, out _);

    /// <summary>
    /// What this change is worth telling you about, or <see langword="null"/> for the changes that aren't.
    /// A turn only counts as finished if we saw it running: after a restart Fleet knows nothing about a
    /// session until its next event, and a turn that ended before that has already been and gone.
    /// </summary>
    private static string? Reason(string? previous, string activityStatus)
    {
        if (string.Equals(previous, activityStatus, StringComparison.Ordinal))
            return null;

        if (string.Equals(activityStatus, ActivityStatuses.WaitingInput, StringComparison.Ordinal))
            return SessionNotificationReasons.NeedsYou;

        if (string.Equals(activityStatus, ActivityStatuses.Idle, StringComparison.Ordinal)
            && SessionActivityTracker.IsInTurn(previous))
        {
            return SessionNotificationReasons.Finished;
        }

        return null;
    }

    private async Task NotifyAsync(string sessionId, string reason, string? body = null)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var session = await sessions.GetByIdAsync(sessionId).ConfigureAwait(false);
            if (session is null
                || session.IsHidden
                || string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            {
                return;
            }

            // A subagent's own turns aren't the user's to follow: its parent carries the work, and only
            // top-level sessions are on the dashboard.
            if (!string.IsNullOrEmpty(session.ParentSessionId))
                return;

            if (!await preference.IsEnabledAsync(session.UserId, CancellationToken.None).ConfigureAwait(false))
                return;

            // You may have come back while we were reading all that.
            if (focusTracker.IsWatched(sessionId))
                return;

            var payload = new SessionNotificationPayload
            {
                SessionId = sessionId,
                Reason = reason,
                Title = string.IsNullOrWhiteSpace(session.Title) ? "Untitled session" : session.Title,
                Body = body ?? (reason == SessionNotificationReasons.NeedsYou
                    ? "Waiting on your answer."
                    : "Finished its turn."),
            };

            await eventBroadcaster.BroadcastAsync(
                "sessions",
                EventType,
                JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.SessionNotificationPayload),
                session.UserId,
                CancellationToken.None).ConfigureAwait(false);

            LogNotified(sessionId, reason);
        }
        catch (Exception ex)
        {
            LogNotifyFailed(ex, sessionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Notified about session {SessionId} ({Reason})")]
    private partial void LogNotified(string sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not notify about session {SessionId}")]
    private partial void LogNotifyFailed(Exception ex, string sessionId);
}
