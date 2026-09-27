namespace WeaveFleet.Application.Memory;

/// <summary>
/// Where a user's notes are kept, and the files the harnesses read them from. Every call names the user: the bridge
/// saves notes for the session's owner, not for whoever the request came from.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Every note the user has, in no particular order.</summary>
    Task<IReadOnlyList<MemoryNote>> ListAsync(string userId, CancellationToken ct = default);

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
    /// Writes what a session in <paramref name="directory"/> reads: the file named by the SHA-256 of the directory, in
    /// lowercase hex, plus <c>.md</c>, in <see cref="ContextFolder"/>. Unchanged content isn't written again.
    /// </summary>
    Task WriteContextAsync(string userId, string directory, string content, CancellationToken ct = default);

    /// <summary>The session folders the user's context files were written for.</summary>
    Task<IReadOnlyList<string>> ListContextDirectoriesAsync(string userId, CancellationToken ct = default);

    /// <summary>Deletes every context file, so no session reads notes: memory is off.</summary>
    Task ClearContextAsync(string userId, CancellationToken ct = default);
}
