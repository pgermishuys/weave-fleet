using System.Globalization;
using System.Text;

namespace WeaveFleet.Application.Memory;

/// <summary>
/// What the model reads: the rules for keeping notes, then the notes for the session's repository and this machine,
/// each with its id so the agent can correct or forget it. The same text goes to every harness, so the rules live
/// here, once.
/// </summary>
public static class AgentMemoryPrompt
{
    /// <summary>
    /// The notes a session in a repository reads, whole: <see cref="RenderFolder"/> then <see cref="RenderMachine"/>.
    /// <paramref name="canSave"/> is whether the harness has the memory tools; without them the session only reads the
    /// notes.
    /// </summary>
    public static string Render(
        string repository,
        IEnumerable<MemoryNote> repositoryNotes,
        IEnumerable<MemoryNote> machineNotes,
        bool canSave = true)
        => (RenderFolder(repository, repositoryNotes, canSave) + RenderMachine(machineNotes)).TrimEnd() + "\n";

    /// <summary>
    /// The part only a repository's sessions read: the rules, then the repository's notes. A session folder's file holds
    /// this; the machine's notes are in one file every folder shares (<see cref="RenderMachine"/>), so a machine note
    /// changes one file, not every folder's.
    /// </summary>
    public static string RenderFolder(string repository, IEnumerable<MemoryNote> repositoryNotes, bool canSave = true)
    {
        var text = new StringBuilder();
        text.AppendLine("# Fleet memory");
        text.AppendLine();
        text.AppendLine(
            "Fleet keeps notes that earlier sessions learned about this repository and this machine, so you start out knowing "
            + "them. Each note was true when it was saved. Before you rely on a file, command or flag a note names, check it "
            + "still exists.");

        if (canSave)
        {
            text.AppendLine();
            text.AppendLine("Save a note with `fleet_memory_save` when:");
            text.AppendLine(
                "- the user asks you to remember something (\"remember …\", \"note that …\", \"from now on …\", \"don't forget …\"). "
                + "Always save it, with kind \"from-you\", and tell the user you did.");
            text.AppendLine("- the user corrects you or states a preference that will matter again. Kind \"from-you\".");
            text.AppendLine(
                "- a command or tool failed, timed out or was slow, and you found what works. Kind \"learned\". Say what to do, "
                + "not only what went wrong, so the next session gets it right the first time. A learned note lasts "
                + $"{AgentMemory.LearnedLifetimeDays} days of use, so one whose cause is gone drops out. If its failure comes back "
                + "after it has gone, save the lesson again: Fleet recognises it and keeps it longer.");
            text.AppendLine(
                "Don't save what the repository already records (code, docs, git history), details of the task at hand, or "
                + "secrets.");
            text.AppendLine();
            text.AppendLine(
                "Choose the list. \"repository\": it would still be true for this repository on another computer. \"machine\": it "
                + "would still be true for another repository on this computer (disk space, memory, crashes, timeouts, "
                + "installed tools, network).");
            text.AppendLine();
            text.AppendLine(
                "Keep the notes right. One fact per note, in a sentence or two, with dates written out. When a note below is "
                + "wrong or out of date, save the corrected note with `replaces` set to its id, or remove it with "
                + "`fleet_memory_forget`. Never save a note that repeats one below. When a list is full, forget or merge an old "
                + "note first.");
        }

        AppendList(text, $"This repository ({AgentMemory.RepositoryName(repository)})", repositoryNotes);
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// The machine's notes, which every session reads after its folder's part. Starts with a blank line, so the two
    /// read as one text when put together.
    /// </summary>
    public static string RenderMachine(IEnumerable<MemoryNote> machineNotes)
    {
        var text = new StringBuilder();
        AppendList(text, "This machine", machineNotes);
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// What a session hears with its next prompt when its notes changed after it started: its instructions keep the
    /// notes it started with, so this says which ones no longer hold.
    /// </summary>
    public static string RenderChanges(MemoryChanges changes)
    {
        var text = new StringBuilder();
        text.AppendLine("# Fleet memory changed");
        text.AppendLine();
        text.AppendLine("These notes changed since this session started. Where they differ from the notes in your instructions, these are current.");
        foreach (var note in changes.Changed.OrderBy(note => note.Created))
            AppendNote(text, note);
        if (changes.Forgotten.Count > 0)
            text.Append("Forgotten, so no longer true: ").AppendJoin(", ", changes.Forgotten.Select(id => "[" + id + "]")).AppendLine(".");
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>What a session hears with its next prompt when memory was turned off after it started.</summary>
    public const string TurnedOff =
        "Fleet memory has been turned off. Don't rely on the notes under \"Fleet memory\" in your instructions, and don't save new ones.";

    private static void AppendList(StringBuilder text, string heading, IEnumerable<MemoryNote> notes)
    {
        text.AppendLine();
        text.AppendLine("## " + heading);
        var any = false;
        foreach (var note in notes.OrderBy(note => note.Created))
        {
            any = true;
            AppendNote(text, note);
        }

        if (!any)
            text.AppendLine("No notes yet.");
    }

    private static void AppendNote(StringBuilder text, MemoryNote note)
    {
        var date = note.Updated.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        text.Append("- [").Append(note.Id).Append("] ").Append(OneLine(note.Text)).Append(" (").Append(date).AppendLine(")");
    }

    private static string OneLine(string text)
        => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
