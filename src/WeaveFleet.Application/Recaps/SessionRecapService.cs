using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Recaps;

/// <summary>Whether a user has turned session recaps on. Off unless they have.</summary>
public interface IRecapPreference
{
    Task<bool> IsEnabledAsync(string userId, CancellationToken ct);
}

/// <summary>
/// Writes a one-line recap for a session you stepped away from, the way Claude Code does. When a turn
/// ends and no browser tab is looking at the session, a timer fires a few minutes later and asks the
/// harness for a recap off the record: from the whole conversation, without adding to it. That has to
/// happen while the provider still has the conversation cached, so the timer runs from the end of the
/// turn and gives up once the cache is too old. The recap is pushed to the session's topic and kept in
/// memory until your next prompt clears it.
/// </summary>
public sealed partial class SessionRecapService(
    SessionActivityTracker activityTracker,
    InstanceTracker instanceTracker,
    IHarnessRegistry harnessRegistry,
    IEventBroadcaster eventBroadcaster,
    IRecapPreference preference,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<SessionRecapService> logger)
{
    /// <summary>Claude Code's recap prompt, word for word.</summary>
    public const string Prompt =
        "The user stepped away and is coming back. Recap in under 40 words, 1-2 plain sentences, no markdown. " +
        "Lead with the overall goal and current task, then the one next action. Skip root-cause narrative, " +
        "fix internals, secondary to-dos, and em-dash tangents.";

    /// <summary>How long after a turn ends the recap is written (Claude Code: 3 minutes).</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long after a turn ends a recap still reads the conversation from cache. Providers keep it for
    /// 5 minutes; Claude Code stops at 90% of that.
    /// </summary>
    public static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(270);

    /// <summary>The shortest wait after you leave, so switching tabs briefly doesn't start a recap.</summary>
    public static readonly TimeSpan MinimumAway = TimeSpan.FromSeconds(2);

    private const int MaxLength = 400;
    private const int MaxFailuresPerTurn = 3;
    private const int PromptsBeforeFirstRecap = 3;
    private const int PromptsBetweenRecaps = 2;

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    // connection id → the session that tab is looking at
    private readonly ConcurrentDictionary<string, string> _focus = new(StringComparer.Ordinal);

    /// <summary>The session's recap, if Fleet wrote one and no prompt has been sent since.</summary>
    public SessionRecapPayload? Get(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
            return null;

        lock (state)
        {
            return state.Recap is { } recap ? ToPayload(sessionId, recap) : null;
        }
    }

    /// <summary>
    /// Records that a browser tab is looking at a session, or has stopped: the session is open, the tab
    /// is visible and the window has focus. A tab looks at one session at a time.
    /// </summary>
    public void SetFocus(string connectionId, string sessionId, bool focused)
    {
        if (focused)
        {
            string? previous = null;
            _focus.AddOrUpdate(connectionId, sessionId, (_, old) =>
            {
                previous = old;
                return sessionId;
            });

            if (_sessions.TryGetValue(sessionId, out var state))
            {
                lock (state)
                {
                    // Claude Code abandons a recap you came back before; you can read the conversation.
                    state.CancelPending();
                }
            }

            // Switching sessions in one tab is leaving the one you were on.
            if (previous is not null && !string.Equals(previous, sessionId, StringComparison.Ordinal))
                ScheduleIfAway(previous);

            return;
        }

        if (_focus.TryRemove(new KeyValuePair<string, string>(connectionId, sessionId)))
            ScheduleIfAway(sessionId);
    }

    /// <summary>Forgets a disconnected tab, which counts as looking away.</summary>
    public void RemoveConnection(string connectionId)
    {
        if (_focus.TryRemove(connectionId, out var sessionId))
            ScheduleIfAway(sessionId);
    }

    /// <summary>Called for every busy/idle change the harness reports.</summary>
    public void OnActivityChanged(string sessionId, string activityStatus)
    {
        var state = _sessions.GetOrAdd(sessionId, static _ => new SessionState());
        lock (state)
        {
            if (string.Equals(activityStatus, "idle", StringComparison.Ordinal))
            {
                if (state.Busy)
                {
                    state.Busy = false;
                    state.TurnEndedAt = timeProvider.GetUtcNow();
                    state.Failures = 0;
                }
            }
            else
            {
                state.Busy = true;
                state.TurnEndedAt = null;
                state.CancelPending();
                return;
            }
        }

        ScheduleIfAway(sessionId);
    }

    /// <summary>Called after Fleet sends a prompt. Clears the recap: you've replied, so it's done its job.</summary>
    public async Task OnPromptSentAsync(string sessionId, string userId, CancellationToken ct)
    {
        var state = _sessions.GetOrAdd(sessionId, static _ => new SessionState());
        bool cleared;
        lock (state)
        {
            state.UserId = userId;
            state.Prompts++;
            state.PromptsSinceRecap++;
            state.CancelPending();
            cleared = state.Recap is not null;
            state.Recap = null;
        }

        if (cleared)
            await BroadcastAsync(sessionId, userId, new SessionRecapPayload { SessionId = sessionId }, ct).ConfigureAwait(false);
    }

    /// <summary>Drops everything held for a deleted session.</summary>
    public void Forget(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var state))
        {
            lock (state)
            {
                state.CancelPending();
            }
        }
    }

    private bool IsWatched(string sessionId) => _focus.Values.Contains(sessionId, StringComparer.Ordinal);

    private void ScheduleIfAway(string sessionId)
    {
        if (IsWatched(sessionId) || !_sessions.TryGetValue(sessionId, out var state))
            return;

        lock (state)
        {
            if (state.Busy || state.TurnEndedAt is not { } turnEndedAt || state.Timer is not null || state.Writing is not null)
                return;

            var now = timeProvider.GetUtcNow();
            if (now - turnEndedAt >= CacheWindow || !state.HasEnoughPrompts)
                return;

            var due = turnEndedAt + Delay - now;
            if (due < MinimumAway)
                due = MinimumAway;

            state.Timer = timeProvider.CreateTimer(
                static timerState => _ = ((Func<Task>)timerState!)(),
                state: (Func<Task>)(() => WriteAsync(sessionId, state)),
                dueTime: due,
                period: Timeout.InfiniteTimeSpan);
        }
    }

    private async Task WriteAsync(string sessionId, SessionState state)
    {
        CancellationTokenSource writing;
        lock (state)
        {
            state.Timer?.Dispose();
            state.Timer = null;
            if (state.Busy
                || state.TurnEndedAt is not { } turnEndedAt
                || timeProvider.GetUtcNow() - turnEndedAt >= CacheWindow
                || state.Failures >= MaxFailuresPerTurn
                || !state.HasEnoughPrompts
                || IsWatched(sessionId))
            {
                return;
            }

            writing = new CancellationTokenSource();
            state.Writing = writing;
        }

        try
        {
            var text = await AskHarnessAsync(sessionId, writing.Token).ConfigureAwait(false);
            if (text is null)
                return;

            SessionRecap recap;
            string? userId;
            lock (state)
            {
                if (writing.IsCancellationRequested)
                    return;

                recap = new SessionRecap(Truncate(text), timeProvider.GetUtcNow());
                state.Recap = recap;
                state.HasRecapped = true;
                state.PromptsSinceRecap = 0;
                userId = state.UserId;
            }

            LogRecapWritten(sessionId);
            await BroadcastAsync(sessionId, userId, ToPayload(sessionId, recap), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (writing.IsCancellationRequested)
        {
            // You came back or sent a prompt while it was being written.
        }
        catch (Exception ex)
        {
            lock (state)
            {
                state.Failures++;
            }

            LogRecapFailed(ex, sessionId);
        }
        finally
        {
            lock (state)
            {
                if (ReferenceEquals(state.Writing, writing))
                    state.Writing = null;
            }

            writing.Dispose();
        }
    }

    private async Task<string?> AskHarnessAsync(string sessionId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var session = await sessions.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is null || string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return null;

        if (_sessions.TryGetValue(sessionId, out var state))
        {
            lock (state)
            {
                state.UserId = session.UserId;
            }
        }

        if (!await preference.IsEnabledAsync(session.UserId, ct).ConfigureAwait(false))
            return null;

        if (harnessRegistry.GetByType(session.HarnessType)?.Capabilities.SupportsOffTheRecordPrompt != true)
            return null;

        // Only a running harness still has the conversation cached; don't wake one up for a recap.
        if (instanceTracker.Get(session.InstanceId) is not { } harness)
            return null;

        // Its children count too: a session whose subagents are still working hasn't finished.
        if (!string.Equals(activityTracker.GetEffectiveActivityStatus(sessionId) ?? "idle", "idle", StringComparison.Ordinal))
            return null;

        var text = await harness.AskOffTheRecordAsync(Prompt, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private Task BroadcastAsync(string sessionId, string? userId, SessionRecapPayload payload, CancellationToken ct)
        => eventBroadcaster.BroadcastAsync(
            $"session:{sessionId}",
            "session.recap",
            JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.SessionRecapPayload),
            new SessionRecapUpdated { Payload = payload },
            userId,
            ct);

    private static SessionRecapPayload ToPayload(string sessionId, SessionRecap recap) => new()
    {
        SessionId = sessionId,
        Text = recap.Text,
        WrittenAt = recap.WrittenAt.ToString("O", CultureInfo.InvariantCulture),
    };

    private static string Truncate(string text)
        => text.Length <= MaxLength ? text : string.Concat(text.AsSpan(0, MaxLength - 1).TrimEnd(), "…");

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrote a recap for session {SessionId}")]
    private partial void LogRecapWritten(string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write a recap for session {SessionId}")]
    private partial void LogRecapFailed(Exception ex, string sessionId);

    private sealed record SessionRecap(string Text, DateTimeOffset WrittenAt);

    /// <summary>What the service knows about one session. Guarded by locking the instance.</summary>
    private sealed class SessionState
    {
        public bool Busy;
        public DateTimeOffset? TurnEndedAt;
        public int Prompts;
        public int PromptsSinceRecap;
        public bool HasRecapped;
        public int Failures;
        public string? UserId;
        public SessionRecap? Recap;
        public ITimer? Timer;
        public CancellationTokenSource? Writing;

        /// <summary>Claude Code's rules: 3 prompts before the first recap, then 2 between recaps.</summary>
        public bool HasEnoughPrompts => HasRecapped
            ? PromptsSinceRecap >= PromptsBetweenRecaps
            : Prompts >= PromptsBeforeFirstRecap;

        public void CancelPending()
        {
            Timer?.Dispose();
            Timer = null;
            Writing?.Cancel();
        }
    }
}
