using System.Data;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IMessageRepository
{
    /// <summary>
    /// Upsert a message. Uses INSERT OR REPLACE to always keep the latest
    /// version (complete response, not the initial skeleton from message.created).
    /// </summary>
    Task UpsertAsync(PersistedMessage message);

    /// <summary>
    /// Upsert a message on an existing transaction.
    /// </summary>
    Task UpsertAsync(IDbConnection connection, IDbTransaction? transaction, PersistedMessage message);

    /// <summary>
    /// Batch upsert multiple messages in a single transaction.
    /// </summary>
    Task UpsertBatchAsync(IReadOnlyList<PersistedMessage> messages);

    /// <summary>
    /// Batch upsert multiple messages using an existing transaction.
    /// </summary>
    Task UpsertBatchAsync(IDbConnection connection, IDbTransaction transaction, IReadOnlyList<PersistedMessage> messages);

    /// <summary>
    /// Retrieve messages for a session with cursor-based pagination.
    /// Returns messages ordered by timestamp ascending (oldest first),
    /// matching the order returned by OpenCode's live API.
    /// </summary>
    Task<IReadOnlyList<PersistedMessage>> GetBySessionAsync(
        string sessionId, int limit, string? beforeMessageId);

    /// <summary>
    /// Count total messages for a session (used to determine hasMore).
    /// </summary>
    Task<int> CountBySessionAsync(string sessionId);

    /// <summary>
    /// Check if any messages exist for a session.
    /// </summary>
    Task<bool> HasMessagesAsync(string sessionId);

    /// <summary>
    /// Retrieve a single message by its composite key (id, session_id).
    /// Returns null if no matching message exists.
    /// </summary>
    Task<PersistedMessage?> GetByIdAsync(string id, string sessionId);

    /// <summary>
    /// Delete all messages for a session (called when session is deleted).
    /// Note: ON DELETE CASCADE handles this at DB level, but explicit
    /// method useful for testing and non-cascade scenarios.
    /// </summary>
    Task DeleteBySessionAsync(string sessionId);
}
