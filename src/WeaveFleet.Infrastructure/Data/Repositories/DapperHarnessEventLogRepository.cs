using System.Data;
using System.Data.Common;
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
        var inserted = connection.ExecuteScalarAsync<long?>(
            """
            INSERT INTO harness_events (session_id, sequence_number, type, payload, user_id, created_at)
            VALUES (@SessionId, @SequenceNumber, @Type, @Payload, @UserId, @CreatedAt)
            ON CONFLICT(session_id, sequence_number) DO NOTHING
            RETURNING id
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", entry.SessionId);
                cmd.AddParameter("SequenceNumber", entry.SequenceNumber);
                cmd.AddParameter("Type", entry.Type);
                cmd.AddParameter("Payload", entry.Payload);
                cmd.AddParameter("UserId", entry.UserId);
                cmd.AddParameter("CreatedAt", entry.CreatedAt);
            },
            transaction).ConfigureAwait(false);

        if (await inserted is { } id) return id;

        return connection.ExecuteScalarAsync<long>(
            "SELECT id FROM harness_events WHERE session_id = @SessionId AND sequence_number = @SequenceNumber",
            cmd =>
            {
                cmd.AddParameter("SessionId", entry.SessionId);
                cmd.AddParameter("SequenceNumber", entry.SequenceNumber);
            },
            transaction).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public async Task<IReadOnlyList<HarnessEventLogEntry>> GetBySessionAfterAsync(
        string sessionId,
        long afterSequenceNumber,
        int limit)
    {
        using var connection = connectionFactory.CreateConnection();
        return await connection.QueryAsync(
            """
            SELECT id, session_id, sequence_number, type, payload, user_id, created_at
            FROM harness_events
            WHERE session_id = @SessionId
              AND sequence_number > @AfterSequenceNumber
              AND (user_id = @UserId OR user_id IS NULL)
            ORDER BY sequence_number ASC
            LIMIT @Limit
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("AfterSequenceNumber", afterSequenceNumber);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("Limit", limit);
            },
            ReadHarnessEventLogEntry).ConfigureAwait(false);
    }

    private static HarnessEventLogEntry ReadHarnessEventLogEntry(DbDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        SequenceNumber = r.GetInt64(r.GetOrdinal("sequence_number")),
        Type = r.GetString(r.GetOrdinal("type")),
        Payload = r.GetString(r.GetOrdinal("payload")),
        UserId = r.GetNullableString(r.GetOrdinal("user_id")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
    };
}
