using System.Collections.Concurrent;

namespace WeaveFleet.Application.Browser;

/// <summary>
/// Which of a session's tool calls is using the browser right now, so each step is filed under the call that made it
/// and the conversation lists it there. A harness's browser commands don't say which call sent them (OpenCode 2's
/// plugin passes a request id, not the tool call), but only a running call can send them; Fleet sees calls start and
/// end, and keeps the latest running one that can use the browser.
/// </summary>
public sealed class AgentBrowserCalls
{
    /// <summary>The tools whose calls can take browser steps: OpenCode 2's Code Mode, and Fleet's own browser tools.</summary>
    public static bool UsesBrowser(string toolName) => toolName is "execute" or "fleet_browser_read" or "fleet_browser_act";

    private readonly ConcurrentDictionary<string, string> _running = new(StringComparer.Ordinal);

    public void Started(string sessionId, string callId) => _running[sessionId] = callId;

    public void Ended(string sessionId, string callId)
        => _running.TryRemove(new KeyValuePair<string, string>(sessionId, callId));

    /// <summary>The call using the browser in <paramref name="sessionId"/>, or null when none is running.</summary>
    public string? Current(string sessionId) => _running.GetValueOrDefault(sessionId);
}
