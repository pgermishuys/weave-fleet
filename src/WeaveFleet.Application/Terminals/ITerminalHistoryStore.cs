namespace WeaveFleet.Application.Terminals;

/// <summary>A terminal Fleet remembers for a session, so its tab and scrollback come back after a restart.</summary>
public sealed record SavedTerminal(string Id, string Title, DateTimeOffset CreatedAt);

/// <summary>
/// Keeps each session's terminals and their scrollback on disk. Ids are used as file names, so they must be
/// 1–128 characters of letters, digits, <c>-</c>, <c>_</c> and <c>.</c>; anything else throws.
/// </summary>
public interface ITerminalHistoryStore
{
    /// <summary>The session's terminals, oldest first. Empty when there are none.</summary>
    Task<IReadOnlyList<SavedTerminal>> ListAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Adds the terminal to the session's list, or updates it when it's already there.</summary>
    Task SaveTerminalAsync(string sessionId, SavedTerminal terminal, CancellationToken ct = default);

    /// <summary>Removes the terminal from the list and deletes its scrollback.</summary>
    Task RemoveTerminalAsync(string sessionId, string terminalId, CancellationToken ct = default);

    /// <summary>The saved scrollback, or null when there isn't any.</summary>
    Task<byte[]?> ReadHistoryAsync(string sessionId, string terminalId, CancellationToken ct = default);

    /// <summary>Replaces the saved scrollback. The write is atomic: a crash leaves the old or the new file.</summary>
    Task WriteHistoryAsync(string sessionId, string terminalId, ReadOnlyMemory<byte> history, CancellationToken ct = default);

    /// <summary>Deletes everything saved for the session.</summary>
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
}
