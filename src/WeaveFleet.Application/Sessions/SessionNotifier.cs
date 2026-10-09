using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Tells you about a session while you're looking somewhere else. The moments worth interrupting for: the agent
/// waits on you (a permission or a question), a turn ended, or it failed. Whether you're looking is the same signal
/// the session recap waits on — a tab that has the session open, visible and focused — so Fleet is quiet about the
/// session in front of you and speaks up about the ones behind it.
/// <para>
/// What to send is decided here; where it goes is up to the <see cref="ISessionNotificationSink"/>s: the open tabs
/// (which apply the desktop setting themselves) and phones (which apply their own choices).
/// </para>
/// </summary>
public sealed partial class SessionNotifier(
    SessionFocusTracker focusTracker,
    IEnumerable<ISessionNotificationSink> sinks,
    IServiceScopeFactory scopeFactory,
    ILogger<SessionNotifier> logger,
    IPendingPermissions? pendingPermissions = null,
    MachineIdentityStore? machine = null,
    TimeProvider? time = null)
{
    /// <summary>The event type on the global sessions topic.</summary>
    public const string EventType = "session_notification";

    /// <summary>The same notification twice within this long is sent once.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(30);

    private const int MaxBodyLength = 140;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ISessionNotificationSink[] _sinks = [.. sinks];

    // session id → the last activity status we were told about
    private readonly ConcurrentDictionary<string, string> _last = new(StringComparer.Ordinal);

    // session id → the last notification sent, to skip repeats and the "finished" that follows a failure
    private readonly ConcurrentDictionary<string, Sent> _sent = new(StringComparer.Ordinal);

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

        if (reason == SessionNotificationReasons.NeedsYou)
        {
            // A permission ask names what the agent wants to do; anything else waiting is a question.
            var waiting = pendingPermissions?.WaitingIn(sessionId);
            var ask = waiting is { Count: > 0 } ? waiting[0] : null;
            if (ask is not null)
                Send(sessionId, reason, SessionNotificationKinds.Permission, WantsTo(ask), ask.Id);
            else
                Send(sessionId, reason, SessionNotificationKinds.Question, "Asked you a question.");
            return;
        }

        Send(sessionId, reason, SessionNotificationKinds.Finished, "Finished its turn.");
    }

    /// <summary>
    /// A workflow run stopped on the user, with its card in this session: a You step, or a step that stopped without
    /// an outcome. Waiting on the user is the run's state, not the session's, so it doesn't come as an activity change.
    /// </summary>
    public void OnWorkflowNeedsYou(string sessionId, string body)
    {
        if (focusTracker.IsWatched(sessionId))
            return;

        Send(sessionId, SessionNotificationReasons.NeedsYou, SessionNotificationKinds.Workflow, body);
    }

    /// <summary>A turn failed, or the session couldn't start. <paramref name="message"/> is the short reason.</summary>
    public void OnSessionFailed(string sessionId, string? message)
    {
        if (focusTracker.IsWatched(sessionId))
            return;

        var body = string.IsNullOrWhiteSpace(message) ? "Stopped with an error." : $"Stopped: {message}";
        Send(sessionId, SessionNotificationReasons.Failed, SessionNotificationKinds.Failed, body);
    }

    /// <summary>Drops what's held for a deleted session.</summary>
    public void Forget(string sessionId)
    {
        _last.TryRemove(sessionId, out _);
        _sent.TryRemove(sessionId, out _);
    }

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

    private static string WantsTo(PermissionAsk ask)
    {
        var what = string.IsNullOrWhiteSpace(ask.Title) ? ask.Tool : ask.Title;
        return ask.Kind switch
        {
            PermissionKinds.Shell => $"Wants to run {what}",
            PermissionKinds.Edit => $"Wants to edit {what}",
            PermissionKinds.Read => $"Wants to read {what}",
            PermissionKinds.Web => $"Wants to open {what}",
            _ => $"Wants to use {what}",
        };
    }

    private void Send(string sessionId, string reason, string kind, string body, string? requestId = null)
    {
        var now = _time.GetUtcNow();
        var next = new Sent(kind, requestId, now);
        var skip = false;
        _sent.AddOrUpdate(
            sessionId,
            next,
            (_, previous) =>
            {
                var recent = now - previous.At < RepeatWindow;
                // The same thing again, or the idle that follows a failed turn: already said.
                skip = recent && ((previous.Kind == kind && previous.RequestId == requestId)
                    || (previous.Kind == SessionNotificationKinds.Failed && kind == SessionNotificationKinds.Finished));
                return skip ? previous : next;
            });
        if (skip)
            return;

        _ = NotifyAsync(sessionId, reason, kind, body, requestId);
    }

    private async Task NotifyAsync(string sessionId, string reason, string kind, string body, string? requestId)
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

            // You may have come back while we were reading all that.
            if (focusTracker.IsWatched(sessionId))
                return;

            var identity = machine?.Get();
            var payload = new SessionNotificationPayload
            {
                SessionId = sessionId,
                Reason = reason,
                Kind = kind,
                RequestId = requestId,
                MachineId = identity?.Id,
                MachineName = identity is null ? null : identity.Name ?? Environment.MachineName,
                Title = string.IsNullOrWhiteSpace(session.Title) ? "Untitled session" : session.Title,
                Body = Clip(body),
            };

            foreach (var sink in _sinks)
            {
                try
                {
                    await sink.HandleAsync(payload, session.UserId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogSinkFailed(ex, sink.GetType().Name, sessionId);
                }
            }

            LogNotified(sessionId, kind);
        }
        catch (Exception ex)
        {
            LogNotifyFailed(ex, sessionId);
        }
    }

    private static string Clip(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxBodyLength ? line : string.Concat(line.AsSpan(0, MaxBodyLength - 1).TrimEnd(), "…");
    }

    private sealed record Sent(string Kind, string? RequestId, DateTimeOffset At);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Notified about session {SessionId} ({Kind})")]
    private partial void LogNotified(string sessionId, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not notify about session {SessionId}")]
    private partial void LogNotifyFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification sink {Sink} failed for session {SessionId}")]
    private partial void LogSinkFailed(Exception ex, string sink, string sessionId);
}
