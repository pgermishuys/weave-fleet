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

    // session id → how many times the user has touched it (opened, left, prompted)
    private readonly ConcurrentDictionary<string, int> _touched = new(StringComparer.Ordinal);

    /// <summary>
    /// Counts up each time the user touches the session: a tab opens or leaves it, or they send it a prompt.
    /// A notification that was sent at one count and is followed by the same count has not been seen.
    /// </summary>
    public int Touches(string sessionId) => _touched.TryGetValue(sessionId, out var n) ? n : 0;

    /// <summary>Records that the user touched the session, so what Fleet told them about it has been seen.</summary>
    public void Touch(string sessionId) => _touched.AddOrUpdate(sessionId, 1, static (_, n) => n + 1);

    /// <summary>Whether any tab is looking at the session right now.</summary>
    public bool IsWatched(string sessionId) => _focus.Values.Contains(sessionId, StringComparer.Ordinal);

    /// <summary>
    /// Records that a tab is looking at a session, or has stopped. Returns the session the tab left, if any:
    /// switching sessions in one tab is leaving the one it was on.
    /// </summary>
    public string? SetFocus(string connectionId, string sessionId, bool focused)
    {
        Touch(sessionId);
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
    {
        if (!_focus.TryRemove(connectionId, out var sessionId))
            return null;
        Touch(sessionId);
        return sessionId;
    }
}
