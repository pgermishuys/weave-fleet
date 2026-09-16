using System.Collections.Concurrent;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Which session each browser tab is looking at: the session is open, the tab is visible and the window has
/// focus. A tab looks at one session at a time. Everything that has to know whether the user is there — the
/// session recap, the notifications Fleet sends when they aren't — reads it from here, so they can never
/// disagree about it.
/// </summary>
public sealed class SessionFocusTracker
{
    // connection id → the session that tab is looking at
    private readonly ConcurrentDictionary<string, string> _focus = new(StringComparer.Ordinal);

    /// <summary>Whether any tab is looking at the session right now.</summary>
    public bool IsWatched(string sessionId) => _focus.Values.Contains(sessionId, StringComparer.Ordinal);

    /// <summary>
    /// Records that a tab is looking at a session, or has stopped. Returns the session the tab left, if any:
    /// switching sessions in one tab is leaving the one it was on.
    /// </summary>
    public string? SetFocus(string connectionId, string sessionId, bool focused)
    {
        if (!focused)
            return _focus.TryRemove(new KeyValuePair<string, string>(connectionId, sessionId)) ? sessionId : null;

        string? previous = null;
        _focus.AddOrUpdate(connectionId, sessionId, (_, old) =>
        {
            previous = old;
            return sessionId;
        });

        return string.Equals(previous, sessionId, StringComparison.Ordinal) ? null : previous;
    }

    /// <summary>Forgets a disconnected tab, which counts as looking away. Returns the session it was looking at.</summary>
    public string? RemoveConnection(string connectionId)
        => _focus.TryRemove(connectionId, out var sessionId) ? sessionId : null;
}
