using System.Collections.Concurrent;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Shared accumulator for <c>message.part.delta</c> text fragments. Held as a singleton so
/// deltas buffered by the ephemeral relay survive across scoped projection invocations and
/// the subsequent <c>message.updated</c> persistence call can merge them into the final row.
/// </summary>
public sealed class TextDeltaBuffer
{
    // Key format: ({fleetSessionId}, {messageId}, {partId}) → accumulated text.
    private readonly ConcurrentDictionary<(string SessionId, string MessageId, string PartId), string> _buffer = new();

    public void Append(string fleetSessionId, string messageId, string partId, string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        _buffer.AddOrUpdate(
            (fleetSessionId, messageId, partId),
            delta,
            (_, existing) => existing + delta);
    }

    /// <summary>Returns all buffered (messageId, partId) → text entries for the session.</summary>
    public IReadOnlyDictionary<(string MessageId, string PartId), string> SnapshotSession(string fleetSessionId)
    {
        var result = new Dictionary<(string, string), string>();
        foreach (var kv in _buffer)
        {
            if (kv.Key.SessionId == fleetSessionId)
                result[(kv.Key.MessageId, kv.Key.PartId)] = kv.Value;
        }
        return result;
    }

    /// <summary>Remove buffered entries for a specific part.</summary>
    public void ClearPart(string fleetSessionId, string messageId, string partId)
        => _buffer.TryRemove((fleetSessionId, messageId, partId), out _);

    /// <summary>Remove buffered entries for a specific message (all its parts).</summary>
    public void ClearMessage(string fleetSessionId, string messageId)
    {
        foreach (var key in _buffer.Keys.Where(k => k.SessionId == fleetSessionId && k.MessageId == messageId).ToArray())
            _buffer.TryRemove(key, out _);
    }
}
