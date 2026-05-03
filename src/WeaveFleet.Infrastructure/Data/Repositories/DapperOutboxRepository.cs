using System.Data;
using System.Data.Common;
using System.Text;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperOutboxRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IOutboxRepository
{
    public async Task<long> EnqueueAsync(OutboxMessage message)
    {
        using var connection = connectionFactory.CreateConnection();
        return await EnqueueAsync(connection, transaction: null, message).ConfigureAwait(false);
    }

    public async Task<long> EnqueueAsync(IDbConnection connection, IDbTransaction? transaction, OutboxMessage message)
    {
        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO outbox_messages (topic, type, payload, user_id, created_at, available_at, dispatched_at)
            VALUES (@Topic, @Type, @Payload, @UserId, @CreatedAt, @AvailableAt, @DispatchedAt)
            RETURNING id
            """,
            cmd =>
            {
                cmd.AddParameter("Topic", message.Topic);
                cmd.AddParameter("Type", message.Type);
                cmd.AddParameter("Payload", message.Payload);
                cmd.AddParameter("UserId", message.UserId);
                cmd.AddParameter("CreatedAt", message.CreatedAt);
                cmd.AddParameter("AvailableAt", message.AvailableAt);
                cmd.AddParameter("DispatchedAt", message.DispatchedAt);
            },
            transaction).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetUndispatchedAsync(int limit)
    {
        using var connection = connectionFactory.CreateConnection();
        return await connection.QueryAsync(
            """
            SELECT id, topic, type, payload, user_id, created_at, available_at, dispatched_at
            FROM outbox_messages
            WHERE dispatched_at IS NULL
              AND available_at <= @Now
            ORDER BY id ASC
            LIMIT @Limit
            """,
            cmd =>
            {
                cmd.AddParameter("Now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.AddParameter("Limit", limit);
            },
            ReadOutboxMessage).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetByTopicAfterAsync(string topic, long sequenceNumber, int limit)
    {
        using var connection = connectionFactory.CreateConnection();
        return await connection.QueryAsync(
            """
            SELECT id, topic, type, payload, user_id, created_at, available_at, dispatched_at
            FROM outbox_messages
            WHERE topic = @Topic
              AND id > @SequenceNumber
              AND (user_id = @UserId OR user_id IS NULL)
            ORDER BY id ASC
            LIMIT @Limit
            """,
            cmd =>
            {
                cmd.AddParameter("Topic", topic);
                cmd.AddParameter("SequenceNumber", sequenceNumber);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("Limit", limit);
            },
            ReadOutboxMessage).ConfigureAwait(false);
    }

    public async Task MarkDispatchedAsync(IReadOnlyList<long> ids, string dispatchedAt)
    {
        if (ids.Count == 0)
            return;

        using var connection = connectionFactory.CreateConnection();
        var dbConn = (DbConnection)connection;
        await using var cmd = dbConn.CreateCommand();
        var sql = new StringBuilder(
            """
            UPDATE outbox_messages
            SET dispatched_at = @DispatchedAt
            WHERE id 
            """);
        cmd.AddParameter("DispatchedAt", dispatchedAt);
        SqlInExpander.AppendInClause(sql, cmd, "Id", ids);
        sql.Append("""

              AND dispatched_at IS NULL
            """);
        cmd.CommandText = sql.ToString();
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<int> DeleteDispatchedBeforeAsync(string dispatchedBefore, int limit)
    {
        using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteNonQueryAsync(
            """
            DELETE FROM outbox_messages
            WHERE id IN (
                SELECT id
                FROM outbox_messages
                WHERE dispatched_at IS NOT NULL
                  AND dispatched_at < @DispatchedBefore
                ORDER BY dispatched_at ASC, id ASC
                LIMIT @Limit
            )
            """,
            cmd =>
            {
                cmd.AddParameter("DispatchedBefore", dispatchedBefore);
                cmd.AddParameter("Limit", limit);
            }).ConfigureAwait(false);
    }

    private static OutboxMessage ReadOutboxMessage(DbDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Topic = r.GetString(r.GetOrdinal("topic")),
        Type = r.GetString(r.GetOrdinal("type")),
        Payload = r.GetString(r.GetOrdinal("payload")),
        UserId = r.GetNullableString(r.GetOrdinal("user_id")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        AvailableAt = r.GetString(r.GetOrdinal("available_at")),
        DispatchedAt = r.GetNullableString(r.GetOrdinal("dispatched_at")),
    };
}
