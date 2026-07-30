namespace WeaveFleet.Application.Services;

/// <summary>
/// Snapshot of a session's streaming state, combining activity status and in-flight text deltas.
/// </summary>
public sealed record StreamingStateSnapshot(
    SessionActivitySnapshot? ActivitySnapshot,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BufferedDeltas);

/// <summary>
/// Composes <see cref="SessionActivityTracker"/> and <see cref="TextDeltaBuffer"/> to provide
/// a unified snapshot of a session's streaming state: activity status + in-flight text deltas.
/// Used by SignalR hubs to merge ephemeral state onto persisted messages.
/// </summary>
public sealed class StreamingStateProvider
{
    private readonly SessionActivityTracker _activityTracker;
    private readonly TextDeltaBuffer _deltaBuffer;

    public StreamingStateProvider(
        SessionActivityTracker activityTracker,
        TextDeltaBuffer deltaBuffer)
    {
        _activityTracker = activityTracker;
        _deltaBuffer = deltaBuffer;
    }

    /// <summary>
    /// Returns the streaming state for a session: activity status + buffered text deltas.
    /// Returns empty/idle state when no deltas are buffered and no activity is tracked.
    /// </summary>
    public StreamingStateSnapshot GetStreamingState(string fleetSessionId)
    {
        var activitySnapshot = _activityTracker.Get(fleetSessionId);
        var rawDeltas = _deltaBuffer.SnapshotSession(fleetSessionId);

        // Transform flat (messageId, partId) → text into messageId → { partId → text }
        var bufferedDeltas = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        foreach (var ((messageId, partId), text) in rawDeltas)
        {
            if (!bufferedDeltas.TryGetValue(messageId, out var parts))
            {
                parts = new Dictionary<string, string>();
                bufferedDeltas[messageId] = parts;
            }

            ((Dictionary<string, string>)parts)[partId] = text;
        }

        return new StreamingStateSnapshot(activitySnapshot, bufferedDeltas);
    }
}
