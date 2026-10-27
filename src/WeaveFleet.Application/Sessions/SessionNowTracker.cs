using System.Collections.Concurrent;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Sessions;

/// <summary>What a session is doing this second: <see cref="Kind"/> is <c>tool</c>, <c>thinking</c> or <c>reply</c>.</summary>
public sealed record SessionNow(string Text, string Kind, DateTimeOffset At);

/// <summary>
/// Keeps each session's latest <see cref="SessionNow"/> line for mission control. Ephemeral and in memory. It keeps
/// nothing until the first client asks (<see cref="Activate"/>), so a Fleet nobody opens mission control on does no
/// extra work; after that it stays on until restart.
/// </summary>
public sealed class SessionNowTracker
{
    private readonly ConcurrentDictionary<string, (string? UserId, SessionNow Now)> _lines = new(StringComparer.Ordinal);
    private volatile bool _active;

    /// <summary>Whether a client has asked yet. The relay checks it before calling <see cref="Observe"/>.</summary>
    public bool IsActive => _active;

    public void Activate() => _active = true;

    public void Observe(string sessionId, string? userId, DomainEvent? domainEvent)
    {
        if (domainEvent is not MessagePartUpdated { Payload.Part: { } part })
            return;

        SessionNow? now = part switch
        {
            ToolMessageEventPart tool => new(NowSummary.FromTool(tool.ToolName, InputOf(tool.State)), "tool", DateTimeOffset.UtcNow),
            ReasoningMessageEventPart => new(NowSummary.Thinking, "thinking", DateTimeOffset.UtcNow),
            TextMessageEventPart { Text: { Length: > 0 } text } => new(NowSummary.FromReply(text), "reply", DateTimeOffset.UtcNow),
            _ => null,
        };
        if (now is not null && now.Text.Length > 0)
            _lines[sessionId] = (userId, now);
    }

    public void Forget(string sessionId) => _lines.TryRemove(sessionId, out _);

    /// <summary>The lines of <paramref name="userId"/>'s sessions, by session id.</summary>
    public IReadOnlyDictionary<string, SessionNow> GetFor(string userId) =>
        _lines.Where(l => l.Value.UserId == userId).ToDictionary(l => l.Key, l => l.Value.Now, StringComparer.Ordinal);

    private static System.Text.Json.JsonElement? InputOf(ToolInvocationState state) => state switch
    {
        ToolPendingState s => s.Input,
        ToolRunningState s => s.Input,
        ToolCompletedState s => s.Input,
        ToolErrorState s => s.Input,
        ToolCancelledState s => s.Input,
        _ => null,
    };
}
