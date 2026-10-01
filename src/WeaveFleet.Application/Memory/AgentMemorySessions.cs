namespace WeaveFleet.Application.Memory;

/// <summary>What changed in a session's notes since it was last told: notes added or reworded, and notes forgotten.</summary>
public sealed record MemoryChanges(IReadOnlyList<MemoryNote> Changed, IReadOnlyList<string> Forgotten);

/// <summary>
/// The notes each running session's model has been given. A session's system prompt keeps the notes it started with:
/// changing it mid-session would throw away the prompt cache for the whole conversation. So a change after that reaches
/// the session as a note with its next prompt (<see cref="AgentMemoryService.ChangesForAsync"/>), worked out here.
/// Kept in memory: after Fleet restarts, the harness processes start again and read the notes afresh, and so does this.
/// </summary>
public sealed class AgentMemorySessions
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _told = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that <paramref name="sessionId"/> now knows <paramref name="notes"/>, and returns what changed since it
    /// was last told. <see langword="null"/> when nothing did, and on the session's first prompt, whose instructions
    /// already hold the notes. A note it knew that's in <paramref name="expired"/> is dropped without a word: it ran
    /// out of days, it wasn't found wrong, so the session isn't told it's no longer true.
    /// </summary>
    public MemoryChanges? Tell(string sessionId, IReadOnlyCollection<MemoryNote> notes, IReadOnlySet<string>? expired = null)
    {
        var now = notes.ToDictionary(note => note.Id, note => note.Text, StringComparer.Ordinal);
        Dictionary<string, string>? before;
        lock (_gate)
        {
            _told.TryGetValue(sessionId, out before);
            _told[sessionId] = now;
        }

        if (before is null)
            return null;

        var changed = notes
            .Where(note => !before.TryGetValue(note.Id, out var text) || !string.Equals(text, note.Text, StringComparison.Ordinal))
            .ToList();
        var forgotten = before.Keys
            .Where(id => !now.ContainsKey(id) && expired?.Contains(id) != true)
            .Order(StringComparer.Ordinal)
            .ToList();
        return changed.Count == 0 && forgotten.Count == 0 ? null : new MemoryChanges(changed, forgotten);
    }

    /// <summary>The session's agent saved <paramref name="note"/> itself, in place of <paramref name="replaced"/>: it knows.</summary>
    public void Saved(string sessionId, MemoryNote note, string? replaced)
    {
        lock (_gate)
        {
            if (!_told.TryGetValue(sessionId, out var known))
                return;
            if (replaced is not null)
                known.Remove(replaced);
            known[note.Id] = note.Text;
        }
    }

    /// <summary>The session's agent forgot <paramref name="noteId"/> itself: it knows.</summary>
    public void Forgot(string sessionId, string noteId)
    {
        lock (_gate)
        {
            if (_told.TryGetValue(sessionId, out var known))
                known.Remove(noteId);
        }
    }

    /// <summary>
    /// Memory was turned off: stops tracking <paramref name="sessionId"/>. Returns whether the session had been given
    /// notes, so it can be told not to rely on them.
    /// </summary>
    public bool TurnedOff(string sessionId)
    {
        lock (_gate)
            return _told.Remove(sessionId);
    }
}
