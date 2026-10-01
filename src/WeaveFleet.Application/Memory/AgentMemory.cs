using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Memory;

/// <summary>
/// Agent memory: notes agents keep about a repository and about this machine, so every new session starts out knowing
/// what earlier ones learned. Off until the user turns it on in Settings → Memory. The notes live in Fleet's data folder
/// (<see cref="IMemoryStore"/>); each harness puts the notes for a session's folder into its system prompt, and the
/// agent saves notes with the <c>fleet_memory_save</c> tool (<see cref="AgentMemoryBridge"/>).
/// </summary>
public static class AgentMemory
{
    /// <summary>The user preference that turns memory on (<c>true</c>) or off. Unset means off.</summary>
    public const string PreferenceKey = "Memory";

    /// <summary>
    /// Set in a harness process's environment when memory is on: the folder holding each session folder's notes as
    /// rendered for the model (<see cref="IMemoryStore.ContextFolder"/>). Fleet's plugin reads the file for its folder at
    /// each session's first model request, and adds the memory tools only when this is set.
    /// </summary>
    public const string EnvironmentVariable = "FLEET_MEMORY_DIR";

    /// <summary>The event on the <c>sessions</c> topic when an agent saves a note, so Fleet can show it with Undo.</summary>
    public const string SavedEventType = "memory.saved";

    /// <summary>How many notes one repository keeps. A full list makes the agent forget or merge a note first.</summary>
    public const int MaxRepositoryNotes = 40;

    /// <summary>How many notes the machine keeps.</summary>
    public const int MaxMachineNotes = 15;

    /// <summary>The longest note, in characters: a fact in a sentence or two, never a document.</summary>
    public const int MaxNoteLength = 400;

    public static int MaxNotes(MemoryList list) => list == MemoryList.Machine ? MaxMachineNotes : MaxRepositoryNotes;

    /// <summary>The repository a folder's notes belong to: its main checkout, or the folder itself outside git.</summary>
    public static string RepositoryOf(string directory)
        => GitPaths.MainCheckoutOf(directory) ?? Path.GetFullPath(directory).TrimEnd('/', '\\');

    /// <summary>The name a repository is shown by: its folder's name.</summary>
    public static string RepositoryName(string repository)
        => Path.GetFileName(repository.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : repository;
}

/// <summary>Which list a note is in.</summary>
public enum MemoryList
{
    /// <summary>True for this repository on any computer: test commands, conventions, the user's corrections about the code.</summary>
    Repository,

    /// <summary>True for any repository on this computer: disk, memory, crashes, timeouts, installed tools, network.</summary>
    Machine,
}

/// <summary>Where a note came from, as Settings shows it.</summary>
public static class MemoryKinds
{
    /// <summary>The agent learned it: something failed, timed out or was slow, and it found what works.</summary>
    public const string Learned = "learned";

    /// <summary>The agent saved it because the user said so: "remember …", a correction, a preference.</summary>
    public const string FromYou = "from-you";

    /// <summary>The user wrote it in Settings.</summary>
    public const string Added = "added";

    /// <summary>The kinds an agent may give a note.</summary>
    public static bool IsAgentKind(string? kind) => kind is Learned or FromYou;
}

/// <summary>One note.</summary>
/// <param name="Repository">The repository a repository note belongs to (<see cref="AgentMemory.RepositoryOf"/>); none for a machine note.</param>
/// <param name="SessionId">The session that saved it; none when the user added it.</param>
/// <param name="SessionTitle">That session's title when it saved the note.</param>
public sealed record MemoryNote(
    string Id,
    MemoryList List,
    string Text,
    string Kind,
    string? Repository,
    string? SessionId,
    string? SessionTitle,
    DateTimeOffset Created,
    DateTimeOffset Updated);

/// <summary>Whether memory is on for the current user.</summary>
public sealed class AgentMemoryFeature(IUserPreferenceRepository preferences)
{
    public async Task<bool> IsEnabledAsync()
        => string.Equals(await preferences.GetAsync(AgentMemory.PreferenceKey).ConfigureAwait(false), "true", StringComparison.OrdinalIgnoreCase);
}
