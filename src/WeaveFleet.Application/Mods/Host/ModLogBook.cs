using System.Collections.Concurrent;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>
/// Each mod's last log lines, per user and mod id: what <c>$.ui.log</c>, <c>console</c>, failures and refused loads wrote.
/// Keeps the last <see cref="Capacity"/> lines of each, in memory. Safe to use from any thread.
/// </summary>
public sealed class ModLogBook
{
    /// <summary>How many lines each mod keeps; older ones drop off.</summary>
    public const int Capacity = 200;

    private readonly ConcurrentDictionary<(string UserId, string ModId), Queue<ModLogLine>> _logs = new();

    public void Add(string userId, string modId, ModLogLine line)
    {
        var lines = _logs.GetOrAdd((userId, modId), _ => new Queue<ModLogLine>());
        lock (lines)
        {
            if (lines.Count == Capacity)
                lines.Dequeue();
            lines.Enqueue(line);
        }
    }

    /// <summary>The mod's lines, oldest first; empty when it has none.</summary>
    public IReadOnlyList<ModLogLine> Read(string userId, string modId)
    {
        if (!_logs.TryGetValue((userId, modId), out var lines))
            return [];
        lock (lines)
            return [.. lines];
    }
}
