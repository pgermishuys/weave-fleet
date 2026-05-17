namespace NuCode.Sessions;

/// <summary>
/// Orchestrates session lifecycle: creation, message/part management, event publishing,
/// and session metadata updates. Wraps the underlying <see cref="ISessionStore"/>
/// and publishes events through the event bus.
/// </summary>
public interface ISessionService
{
    // ── Session lifecycle ──

    /// <summary>Creates a new session with generated ID, slug, and timestamps.</summary>
    Task<NuCodeSession> CreateSessionAsync(string directory, string? title, CancellationToken ct);

    /// <summary>Creates a child session linked to a parent (for subagent/fork).</summary>
    Task<NuCodeSession> CreateChildSessionAsync(SessionId parentId, string directory, string? title, CancellationToken ct);

    /// <summary>Gets a session by ID. Returns null if not found.</summary>
    Task<NuCodeSession?> GetSessionAsync(SessionId id, CancellationToken ct);

    /// <summary>Lists sessions matching the filter.</summary>
    Task<IReadOnlyList<NuCodeSession>> ListSessionsAsync(SessionFilter filter, CancellationToken ct);

    /// <summary>Updates the session title.</summary>
    Task<NuCodeSession> SetTitleAsync(SessionId id, string title, CancellationToken ct);

    /// <summary>Archives a session (sets ArchivedAt).</summary>
    Task<NuCodeSession> ArchiveSessionAsync(SessionId id, CancellationToken ct);

    /// <summary>Unarchives a session (clears ArchivedAt).</summary>
    Task<NuCodeSession> UnarchiveSessionAsync(SessionId id, CancellationToken ct);

    /// <summary>Updates the session-level permission ruleset.</summary>
    Task<NuCodeSession> SetPermissionsAsync(SessionId id, Permissions.PermissionRuleset ruleset, CancellationToken ct);

    /// <summary>Updates the session change summary.</summary>
    Task<NuCodeSession> SetSummaryAsync(SessionId id, SessionSummary summary, CancellationToken ct);

    /// <summary>Sets a revert point on the session.</summary>
    Task<NuCodeSession> SetRevertAsync(SessionId id, SessionRevert revert, CancellationToken ct);

    /// <summary>Clears the revert point from a session.</summary>
    Task<NuCodeSession> ClearRevertAsync(SessionId id, CancellationToken ct);

    /// <summary>Touches the session (updates UpdatedAt).</summary>
    Task<NuCodeSession> TouchSessionAsync(SessionId id, CancellationToken ct);

    /// <summary>Sets the CompactingAt timestamp to indicate compaction is in progress.</summary>
    Task<NuCodeSession> SetCompactingAsync(SessionId id, CancellationToken ct);

    /// <summary>Clears the CompactingAt timestamp after compaction completes.</summary>
    Task<NuCodeSession> ClearCompactingAsync(SessionId id, CancellationToken ct);

    /// <summary>Deletes a session and all its messages/parts.</summary>
    Task DeleteSessionAsync(SessionId id, CancellationToken ct);

    // ── Messages ──

    /// <summary>Adds or updates a message in a session. Publishes message.updated event.</summary>
    Task<NuCodeMessage> UpsertMessageAsync(NuCodeMessage message, CancellationToken ct);

    /// <summary>Gets all messages (with parts) for a session.</summary>
    Task<IReadOnlyList<MessageWithParts>> GetMessagesAsync(SessionId sessionId, CancellationToken ct);

    /// <summary>Gets all messages (with parts) for a session with a limit.</summary>
    Task<IReadOnlyList<MessageWithParts>> GetMessagesAsync(SessionId sessionId, int limit, CancellationToken ct);

    /// <summary>Deletes a message from a session. Publishes message.removed event.</summary>
    Task DeleteMessageAsync(SessionId sessionId, MessageId messageId, CancellationToken ct);

    // ── Parts ──

    /// <summary>Adds or updates a message part. Publishes message.part.updated event.</summary>
    Task<MessagePart> UpsertPartAsync(MessagePart part, CancellationToken ct);

    /// <summary>Publishes a streaming delta for a message part (ephemeral, not persisted).</summary>
    void PublishPartDelta(SessionId sessionId, MessageId messageId, PartId partId, string field, string delta);

    /// <summary>Deletes a message part. Publishes message.part.removed event.</summary>
    Task DeletePartAsync(SessionId sessionId, MessageId messageId, PartId partId, CancellationToken ct);
}
