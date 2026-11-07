namespace WeaveFleet.Application.Memory;

/// <summary>
/// The two groups a list's notes fall into with "Search older notes" on: a short group every session reads, and the rest,
/// found by <c>fleet_memory_search</c> when needed. Everything here is pure: the service decides when to use it.
/// </summary>
public static class MemoryTiers
{
    public static int AlwaysCap(MemoryList list)
        => list == MemoryList.Machine ? AgentMemory.MaxAlwaysMachineNotes : AgentMemory.MaxAlwaysRepositoryNotes;

    public static int SearchCap(MemoryList list)
        => list == MemoryList.Machine ? AgentMemory.MaxSearchMachineNotes : AgentMemory.MaxSearchRepositoryNotes;

    /// <summary>Whether sessions read the note: it isn't kept for search, or the user pinned it.</summary>
    public static bool IsAlways(MemoryNote note) => note.Pinned || !note.InSearch;

    /// <summary>
    /// What a session reads from one list's active notes, and how many more are kept for search.
    /// Search on: the always-read group, the rest searchable. Search off: today's behaviour, every note up to the old cap
    /// (<see cref="AgentMemory.MaxNotes"/>), always-read notes first, then the most found from search.
    /// </summary>
    public static (IReadOnlyList<MemoryNote> Read, int Searchable) Choose(IReadOnlyList<MemoryNote> active, MemoryList list, bool searchOn)
    {
        if (searchOn)
        {
            var read = active.Where(IsAlways).ToList();
            return (read, active.Count - read.Count);
        }

        var cap = AgentMemory.MaxNotes(list);
        if (active.Count <= cap)
            return (active, 0);

        var chosen = active.Where(IsAlways)
            .Concat(active.Where(note => !IsAlways(note)).OrderByDescending(note => note.Found).ThenByDescending(note => note.Updated))
            .Take(cap)
            .ToList();
        return (chosen, 0);
    }

    /// <summary>
    /// The always-read note to move to search to make room: never a pinned one, learned notes before the user's, then the
    /// least found, then the oldest. <see langword="null"/> when every note is pinned.
    /// </summary>
    public static MemoryNote? PickToMoveDown(IEnumerable<MemoryNote> always)
        => always
            .Where(note => !note.Pinned)
            .OrderBy(note => note.Kind == MemoryKinds.Learned ? 0 : 1)
            .ThenBy(note => note.Found)
            .ThenBy(note => note.Updated)
            .FirstOrDefault();

    /// <summary>
    /// The notes that match a query, best first, at most <paramref name="limit"/>. A note's score is the query's words it
    /// has in its text (a point each) and in its "how we know" (half a point), after the same light stemming Fleet uses
    /// to tell that two notes say the same thing; ties go to the note found most, then the newest. No match, no result.
    /// </summary>
    public static IReadOnlyList<MemoryNote> Search(IEnumerable<MemoryNote> notes, string query, int limit)
    {
        var words = MemoryText.Words(query);
        if (words.Count == 0)
            return [];

        return
        [
            .. notes
                .Select(note => (Note: note, Score: Score(note, words)))
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.Note.Found)
                .ThenByDescending(match => match.Note.Updated)
                .Take(limit)
                .Select(match => match.Note),
        ];
    }

    private static double Score(MemoryNote note, HashSet<string> words)
    {
        var text = MemoryText.Words(note.Text);
        var how = note.How is null ? [] : MemoryText.Words(note.How);
        return words.Sum(word => text.Contains(word) ? 1.0 : how.Contains(word) ? 0.5 : 0);
    }
}
