using System.Globalization;
using System.Text;

namespace WeaveFleet.Application.Services.Worktrees;

/// <summary>
/// Turns a message into the <c>{slug}</c> part of a worktree name. Ported from the composer's
/// <c>slugForBranch</c> (<c>client/src/lib/new-session-request.ts</c>); the shared cases in
/// <c>worktree-naming-cases.json</c> are asserted against both so they cannot drift.
/// </summary>
public static class BranchSlug
{
    private const int MaxLength = 40;

    /// <summary>
    /// Words that carry no meaning in a branch name. Dropped unless that would leave nothing.
    /// </summary>
    private static readonly HashSet<string> _stopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "so", "to", "of", "in", "on", "at", "for", "with", "from", "by",
        "into", "about", "as", "is", "are", "be", "it", "its", "this", "that", "these", "those",
        "i", "me", "my", "we", "us", "our", "you", "your", "please", "can", "could", "would", "should", "will",
        "lets", "let", "just", "some", "do", "does",
    };

    /// <summary>
    /// The message's first non-empty line as lowercase words joined by hyphens, filler dropped,
    /// cut at a word boundary to 40 characters. Empty when nothing usable is left, which is the
    /// signal to let the server name the worktree instead.
    /// </summary>
    public static string From(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        var firstLine = FirstNonEmptyLine(message);
        var words = Words(firstLine);
        var meaningful = words.Where(word => !_stopWords.Contains(word)).ToList();
        var chosen = meaningful.Count > 0 ? meaningful : words;

        var slug = string.Empty;
        foreach (var word in chosen)
        {
            var next = slug.Length == 0 ? word : $"{slug}-{word}";
            if (next.Length > MaxLength)
            {
                // A single word longer than the limit is cut; otherwise stop at the last whole word.
                return slug.Length > 0 ? slug : word[..MaxLength];
            }

            slug = next;
        }

        return slug;
    }

    private static string FirstNonEmptyLine(string message)
    {
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.Trim('\r');
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }

        return string.Empty;
    }

    /// <summary>
    /// Lowercase alphanumeric runs, with accents folded to their base letter and apostrophes
    /// dropped so "café's" reads as one word, not two.
    /// </summary>
    private static List<string> Words(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (c is '\'' or '’')
                continue;

            folded.Append(char.ToLowerInvariant(c));
        }

        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var c in folded.ToString())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                word.Append(c);
                continue;
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
            words.Add(word.ToString());

        return words;
    }
}
