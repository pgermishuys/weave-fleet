namespace WeaveFleet.Application.Memory;

/// <summary>
/// Where a user's notes are kept, and the files the harnesses read them from. Every call names the user: the bridge
/// saves notes for the session's owner, not for whoever the request came from. A prompt only needs one repository's
/// notes and the machine's (<see cref="ListForAsync"/>), so a store keeps them apart and answers that without reading
/// the rest.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Every note the user has, in no particular order: for Settings' overview and clearing everything.</summary>
    Task<IReadOnlyList<MemoryNote>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// The machine's notes, and <paramref name="repository"/>'s when there is one: what a session in that repository reads.
    /// </summary>
    Task<IReadOnlyList<MemoryNote>> ListForAsync(string userId, string? repository, CancellationToken ct = default);

    /// <summary>The note with this id, wherever it is kept, or <see langword="null"/>.</summary>
    Task<MemoryNote?> FindAsync(string userId, string id, CancellationToken ct = default);

    /// <summary>Adds the note, or replaces the one with its id.</summary>
    Task SaveAsync(string userId, MemoryNote note, CancellationToken ct = default);

    /// <summary>Deletes the notes with these ids; returns how many there were.</summary>
    Task<int> DeleteAsync(string userId, IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// The folder with the user's notes as the model reads them, one file per session folder
    /// (<see cref="WriteContextAsync"/>). Harness processes get it as <see cref="AgentMemory.EnvironmentVariable"/>.
    /// </summary>
    string ContextFolder(string userId);

    /// <summary>
    /// Writes what only sessions in <paramref name="directory"/> read (the rules and its repository's notes): the file
    /// named by the SHA-256 of the directory, in lowercase hex, plus <c>.md</c>, in <see cref="ContextFolder"/>. Remembers
    /// which repository the folder is in, so a change to that repository's notes rewrites only its folders. Unchanged
    /// content isn't written again.
    /// </summary>
    Task WriteContextAsync(string userId, string directory, string repository, string content, CancellationToken ct = default);

    /// <summary>
    /// Writes the machine's notes, which every session reads after its folder's file: <c>machine.md</c> in
    /// <see cref="ContextFolder"/>. Unchanged content isn't written again.
    /// </summary>
    Task WriteMachineContextAsync(string userId, string content, CancellationToken ct = default);

    /// <summary>The session folders the user's context files were written for, with each one's repository.</summary>
    Task<IReadOnlyList<MemoryContextFolder>> ListContextFoldersAsync(string userId, CancellationToken ct = default);

    /// <summary>Deletes what a session folder reads and forgets the folder: it no longer exists.</summary>
    Task ForgetContextAsync(string userId, string directory, CancellationToken ct = default);

    /// <summary>Deletes every context file, so no session reads notes: memory is off.</summary>
    Task ClearContextAsync(string userId, CancellationToken ct = default);
}

/// <summary>A session folder Fleet writes notes for, and the repository it's in.</summary>
public sealed record MemoryContextFolder(string Directory, string Repository);
