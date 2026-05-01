using System.Data;
using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperHarnessEventLogRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : IHarnessEventLogRepository
{
    public async Task<long> AppendAsync(HarnessEventLogEntry entry)
    {
        using var connection = connectionFactory.CreateConnection();
        return await AppendAsync(connection, transaction: null, entry).ConfigureAwait(false);
    }

    public async Task<long> AppendAsync(IDbConnection connection, IDbTransaction? transaction, HarnessEventLogEntry entry)
    {
        // ON CONFLICT(session_id, sequence_number) DO NOTHING gives us idempotent
        // upserts on JetStream redelivery. RETURNING id needs a SELECT fallback
        // when the row already existed, since DO NOTHING returns no row.
        var inserted = await connection.ExecuteScalarAsync<long?>(
            """
            INSERT INTO harness_events (session_id, sequence_number, type, payload, user_id, created_at)
            VALUES (@SessionId, @SequenceNumber, @Type, @Payload, @UserId, @CreatedAt)
            ON CONFLICT(session_id, sequence_number) DO NOTHING
            RETURNING id
            """,
            new
            {
                entry.SessionId,
                entry.SequenceNumber,
                entry.Type,
                entry.Payload,
                entry.UserId,
                entry.CreatedAt
            },
            transaction).ConfigureAwait(false);

        if (inserted is { } id) return id;

        return await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM harness_events WHERE session_id = @SessionId AND sequence_number = @SequenceNumber",
            new { entry.SessionId, entry.SequenceNumber },
            transaction).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HarnessEventLogEntry>> GetBySessionAfterAsync(
        string sessionId,
        long afterSequenceNumber,
        int limit)
    {
        using var connection = connectionFactory.CreateConnection();
        var results = await connection.QueryAsync<HarnessEventLogEntry>(
            """
            SELECT id, session_id, sequence_number, type, payload, user_id, created_at
            FROM harness_events
            WHERE session_id = @SessionId
              AND sequence_number > @AfterSequenceNumber
              AND (user_id = @UserId OR user_id IS NULL)
            ORDER BY sequence_number ASC
            LIMIT @Limit
            """,
            new
            {
                SessionId = sessionId,
                AfterSequenceNumber = afterSequenceNumber,
                UserId = userContext.UserId,
                Limit = limit
            }).ConfigureAwait(false);

        return results.AsList();
    }
}
