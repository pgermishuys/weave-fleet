namespace NuCode.Sessions;

/// <summary>
/// Persistence layer for sessions, messages, and parts.
/// </summary>
public interface ISessionStore
{
    // ── Session CRUD ──

    /// <summary>Creates a new session.</summary>
    Task<NuCodeSession> CreateSessionAsync(NuCodeSession session, CancellationToken ct);

    /// <summary>Gets a session by ID. Returns null if not found.</summary>
    Task<NuCodeSession?> GetSessionAsync(SessionId id, CancellationToken ct);

    /// <summary>Lists sessions with optional filters, ordered by most recently updated.</summary>
    Task<IReadOnlyList<NuCodeSession>> ListSessionsAsync(SessionFilter filter, CancellationToken ct);

    /// <summary>Updates an existing session.</summary>
    Task<NuCodeSession> UpdateSessionAsync(NuCodeSession session, CancellationToken ct);

    /// <summary>Deletes a session and all its messages/parts (cascade).</summary>
    Task DeleteSessionAsync(SessionId id, CancellationToken ct);

    /// <summary>Gets child sessions of a parent.</summary>
    Task<IReadOnlyList<NuCodeSession>> GetChildSessionsAsync(SessionId parentId, CancellationToken ct);

    // ── Message CRUD ──

    /// <summary>Creates or updates a message (upsert).</summary>
    Task<NuCodeMessage> UpsertMessageAsync(NuCodeMessage message, CancellationToken ct);

    /// <summary>Gets all messages for a session with their parts.</summary>
    Task<IReadOnlyList<MessageWithParts>> GetMessagesAsync(SessionId sessionId, int? limit, CancellationToken ct);

    /// <summary>Removes a message from a session.</summary>
    Task DeleteMessageAsync(SessionId sessionId, MessageId messageId, CancellationToken ct);

    // ── Part CRUD ──

    /// <summary>Creates or updates a message part (upsert).</summary>
    Task<MessagePart> UpsertPartAsync(MessagePart part, CancellationToken ct);

    /// <summary>Removes a part from a message.</summary>
    Task DeletePartAsync(SessionId sessionId, MessageId messageId, PartId partId, CancellationToken ct);

    /// <summary>Gets all parts for a message.</summary>
    Task<IReadOnlyList<MessagePart>> GetPartsAsync(MessageId messageId, CancellationToken ct);
}

/// <summary>
/// Filter criteria for listing sessions.
/// </summary>
public sealed record SessionFilter
{
    /// <summary>Filter to sessions in this directory.</summary>
    public string? Directory { get; init; }

    /// <summary>Only return root sessions (no parent).</summary>
    public bool RootsOnly { get; init; }

    /// <summary>Filter to sessions updated after this time.</summary>
    public DateTimeOffset? UpdatedAfter { get; init; }

    /// <summary>Search text to match against session title.</summary>
    public string? Search { get; init; }

    /// <summary>Maximum number of sessions to return.</summary>
    public int? Limit { get; init; }

    /// <summary>Only return non-archived sessions.</summary>
    public bool ExcludeArchived { get; init; }
}
