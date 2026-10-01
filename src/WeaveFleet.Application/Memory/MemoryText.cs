namespace WeaveFleet.Application.Memory;

/// <summary>
/// Whether two notes say much the same: how Fleet recognises a lesson an agent learned again after its note expired.
/// Agents word the same lesson differently each time, so this compares the words that carry it (commands, tools,
/// paths), not the sentence. A miss only means the lesson starts over with a week; a false match only gives a note a
/// longer life.
/// </summary>
public static class MemoryText
{
    /// <summary>The <see cref="Similarity"/> from which two notes count as the same lesson.</summary>
    public const double SameLesson = 0.6;

    /// <summary>The fewest words two notes share before they can count as the same lesson.</summary>
    private const int FewestShared = 3;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "so", "to", "of", "in", "on", "at", "for", "from", "with", "by", "as",
        "is", "are", "was", "were", "be", "been", "it", "its", "it's", "this", "that", "these", "those", "there", "here",
        "do", "does", "don't", "doesn't", "not", "no", "can", "can't", "cannot", "will", "won't", "would", "should",
        "use", "using", "used", "instead", "rather", "than", "then", "when", "if", "into", "out", "up", "via", "per",
        "has", "have", "had", "which", "what", "who", "all", "any", "every", "also", "only", "just", "always", "never",
        "first", "after", "before", "because", "since", "about", "over", "under", "more", "less", "same", "other",
    };

    /// <summary>
    /// How much two notes share: the words they have in common over the words in the shorter one, from 0 to 1. Zero when
    /// they share fewer than three words, which short notes would otherwise reach by chance.
    /// </summary>
    public static double Similarity(string first, string second)
    {
        var a = Words(first);
        var b = Words(second);
        if (a.Count == 0 || b.Count == 0)
            return 0;

        var shared = a.Count(b.Contains);
        return shared < FewestShared ? 0 : (double)shared / Math.Min(a.Count, b.Count);
    }

    /// <summary>The words that carry a note, lowercase, without filler or punctuation round them.</summary>
    internal static HashSet<string> Words(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('`', '"', '\'', '“', '”', '‘', '’', '(', ')', '[', ']', ',', '.', ';', ':', '!', '?');
            if (word.Length < 2 || Filler.Contains(word))
                continue;
            words.Add(Stem(word));
        }

        return words;
    }

    /// <summary>A plural or tense ending off, so "times out" and "timed out", "repos" and "repo" meet.</summary>
    internal static string Stem(string word)
    {
        if (word.Length > 4 && word.EndsWith("ing", StringComparison.Ordinal))
            word = word[..^3];
        else if (word.Length > 3 && word.EndsWith("ed", StringComparison.Ordinal))
            word = word[..^2];
        else if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
            word = word[..^1];

        // "time", "times", "timed" and "timing" all end up "tim".
        return word.Length > 3 && word.EndsWith('e') ? word[..^1] : word;
    }
}
