using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Sessions;

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

        // One paragraph per cause, in the order the work was lost; work with no recorded cause shares the generic one.
        var groups = lost
            .GroupBy(work => WorkLostCause.IsStoppedFromOutside(work.Detail) ? work.Detail!.Trim() : null)
            .ToList();

        var shown = 0;
        var paragraphs = new List<string>();
        foreach (var group in groups)
        {
            var lines = new List<string> { Heading(group.Key) };
            foreach (var work in group.Take(Math.Max(MaxEntries - shown, 0)))
            {
                lines.Add($"- {work.Kind}: {Describe(work)}");
                shown++;
            }
            if (lines.Count > 1)
                paragraphs.Add(string.Join('\n', lines));
        }
        if (lost.Count > shown)
            paragraphs[^1] += $"\n- and {lost.Count - shown} more";
        return string.Join("\n\n", paragraphs);
    }

    private static string Heading(string? cause) => cause is null
        ? "Note from Fleet: this background work stopped before it finished, because Fleet or the agent's process restarted. "
            + "It was cancelled and won't report back; start it again if it's still needed:"
        : $"Note from Fleet: {cause}, and this background work stopped with it. "
            + "If your commands kill `opencode … serve` processes, exclude $FLEET_HARNESS_SERVER_PIDS. "
            + "It was cancelled and won't report back; start it again if it's still needed:";

    private static string Describe(Delegation work)
    {
        var label = string.IsNullOrWhiteSpace(work.Label) ? work.Title : work.Label;
        var line = string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= MaxLabelLength ? line : line[..(MaxLabelLength - 1)].TrimEnd() + "…";
    }
}
