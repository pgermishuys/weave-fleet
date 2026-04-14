using System.Data;
using Dapper;
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
            new
            {
                message.Topic,
                message.Type,
                message.Payload,
                message.UserId,
                message.CreatedAt,
                message.AvailableAt,
                message.DispatchedAt
            },
            transaction).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetUndispatchedAsync(int limit)
    {
        using var connection = connectionFactory.CreateConnection();
        var results = await connection.QueryAsync<OutboxMessage>(
            """
            SELECT id, topic, type, payload, user_id, created_at, available_at, dispatched_at
            FROM outbox_messages
            WHERE dispatched_at IS NULL
              AND available_at <= @Now
            ORDER BY id ASC
            LIMIT @Limit
            """,
            new
            {
                Now = DateTimeOffset.UtcNow.ToString("O"),
                Limit = limit
            }).ConfigureAwait(false);

        return results.AsList();
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetByTopicAfterAsync(string topic, long sequenceNumber, int limit)
    {
        using var connection = connectionFactory.CreateConnection();
        var results = await connection.QueryAsync<OutboxMessage>(
            """
            SELECT id, topic, type, payload, user_id, created_at, available_at, dispatched_at
            FROM outbox_messages
            WHERE topic = @Topic
              AND id > @SequenceNumber
              AND (user_id = @UserId OR user_id IS NULL)
            ORDER BY id ASC
            LIMIT @Limit
            """,
            new
            {
                Topic = topic,
                SequenceNumber = sequenceNumber,
                UserId = userContext.UserId,
                Limit = limit
            }).ConfigureAwait(false);

        return results.AsList();
    }

    public async Task MarkDispatchedAsync(IReadOnlyList<long> ids, string dispatchedAt)
    {
        if (ids.Count == 0)
            return;

        using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            """
            UPDATE outbox_messages
            SET dispatched_at = @DispatchedAt
            WHERE id IN @Ids
              AND dispatched_at IS NULL
            """,
            new { DispatchedAt = dispatchedAt, Ids = ids }).ConfigureAwait(false);
    }

    public async Task<int> DeleteDispatchedBeforeAsync(string dispatchedBefore, int limit)
    {
        using var connection = connectionFactory.CreateConnection();
        return await connection.ExecuteAsync(
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
            new { DispatchedBefore = dispatchedBefore, Limit = limit }).ConfigureAwait(false);
    }
}
