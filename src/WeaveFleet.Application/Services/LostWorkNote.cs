using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// The one-time note a session's next prompt carries when work its agent left running was lost
/// (<see cref="WorkEndedReasons.Lost"/>: Fleet, or the harness process the work ran in, stopped first). Nothing will
/// report that work back, so without it the agent would wait on a shell, monitor or subagent that's gone. It goes as a
/// note to the model (<see cref="PromptOptions.ModelNotes"/>), so the conversation doesn't show it; T3 Code's
/// <c>RestartBackgroundNote.ts</c> does the same.
/// </summary>
public static class LostWorkNote
{
    /// <summary>The most pieces of work the note names; the rest are counted.</summary>
    public const int MaxEntries = 10;

    /// <summary>The longest a piece of work's description gets in the note.</summary>
    public const int MaxLabelLength = 160;

    /// <summary>The note for <paramref name="lost"/>, or null when there's nothing to tell.</summary>
    public static string? For(IReadOnlyList<Delegation> lost)
    {
        if (lost.Count == 0)
            return null;

        var lines = new List<string>
        {
            "Note from Fleet: this background work stopped before it finished, because Fleet or the agent's process restarted. "
            + "It was cancelled and won't report back; start it again if it's still needed:",
        };
        lines.AddRange(lost.Take(MaxEntries).Select(work => $"- {work.Kind}: {Describe(work)}"));
        if (lost.Count > MaxEntries)
            lines.Add($"- and {lost.Count - MaxEntries} more");
        return string.Join('\n', lines);
    }

    private static string Describe(Delegation work)
    {
        var label = string.IsNullOrWhiteSpace(work.Label) ? work.Title : work.Label;
        var line = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= MaxLabelLength ? line : line[..(MaxLabelLength - 1)].TrimEnd() + "…";
    }
}
