using System.Collections.Concurrent;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// How each session's latest turn ended when it didn't simply finish: the user stopped it, or it failed. A session
/// completion callback reads it, so the session that delegated the work hears the real outcome instead of "completed".
/// A new turn's first reply clears it; in memory, since a Fleet restart ends every turn anyway.
/// </summary>
public sealed class SessionTurnOutcomes
{
    private readonly ConcurrentDictionary<string, TurnError> _outcomes = new(StringComparer.Ordinal);

    /// <summary>The user stopped <paramref name="sessionId"/>'s turn.</summary>
    public void MarkStopped(string sessionId)
        => _outcomes.AddOrUpdate(
            sessionId,
            _ => new TurnError { Name = SessionUpdates.StoppedErrorName, Message = "It was stopped before it finished." },
            (_, existing) => existing);

    /// <summary>The harness reported <paramref name="error"/> for <paramref name="sessionId"/>'s turn. A stop stays a stop.</summary>
    public void MarkFailed(string sessionId, TurnError error)
        => _outcomes.AddOrUpdate(sessionId, _ => error, (_, existing) => existing.Name == SessionUpdates.StoppedErrorName ? existing : error);

    /// <summary>A new turn began: what ended the last one no longer applies.</summary>
    public void Clear(string sessionId) => _outcomes.TryRemove(sessionId, out _);

    /// <summary>How the latest turn ended, or <see langword="null"/> when it finished.</summary>
    public TurnError? Get(string sessionId) => _outcomes.GetValueOrDefault(sessionId);
}
