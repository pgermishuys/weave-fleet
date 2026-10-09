using System.Data.Common;
using System.Globalization;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>The <c>scheduled_retries</c> table (migration 057).</summary>
public sealed class ScheduledRetryRepository(IDbConnectionFactory connectionFactory, IUserContext userContext) : IScheduledRetryRepository
{
    private const string Columns = "session_id, user_id, due_at, attempt, kind, reason, provider_said, state, created_at";

    public async Task<ScheduledRetry?> GetAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"SELECT {Columns} FROM scheduled_retries WHERE session_id = @SessionId AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            Read);
    }

    public async Task<IReadOnlyList<ScheduledRetry>> ListForUserAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            $"SELECT {Columns} FROM scheduled_retries WHERE user_id = @UserId AND state = 'waiting'",
            cmd => cmd.AddParameter("UserId", userContext.UserId),
            Read);
    }

    public async Task<IReadOnlyList<ScheduledRetry>> ListWaitingAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync($"SELECT {Columns} FROM scheduled_retries WHERE state = 'waiting'", _ => { }, Read);
    }

    public async Task<bool> SaveAsync(ScheduledRetry retry)
    {
        using var conn = connectionFactory.CreateConnection();
        var saved = await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO scheduled_retries (session_id, user_id, due_at, attempt, kind, reason, provider_said, state, created_at)
            SELECT @SessionId, @UserId, @DueAt, @Attempt, @Kind, @Reason, @ProviderSaid, @State, @CreatedAt
            FROM sessions
            WHERE id = @SessionId AND user_id = @UserId
            ON CONFLICT (session_id) DO UPDATE SET
              due_at = excluded.due_at, attempt = excluded.attempt, kind = excluded.kind, reason = excluded.reason,
              provider_said = excluded.provider_said, state = excluded.state, created_at = excluded.created_at
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", retry.SessionId);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("DueAt", Format(retry.DueAt));
                cmd.AddParameter("Attempt", retry.Attempt);
                cmd.AddParameter("Kind", retry.Kind);
                cmd.AddParameter("Reason", retry.Reason);
                cmd.AddParameter("ProviderSaid", retry.ProviderSaid ? 1 : 0);
                cmd.AddParameter("State", retry.State);
                cmd.AddParameter("CreatedAt", Format(retry.CreatedAt));
            });
        return saved > 0;
    }

    public async Task<ScheduledRetry?> TakeWaitingAsync(string sessionId)
    {
        // One statement, so two callers can't both send it.
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"""
            UPDATE scheduled_retries SET state = 'sent'
            WHERE session_id = @SessionId AND user_id = @UserId AND state = 'waiting'
            RETURNING {Columns}
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            Read);
    }

    public async Task<ScheduledRetry?> RemoveAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"DELETE FROM scheduled_retries WHERE session_id = @SessionId AND user_id = @UserId RETURNING {Columns}",
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            Read);
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

    private static ScheduledRetry Read(DbDataReader r) => new()
    {
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        DueAt = Parse(r.GetString(r.GetOrdinal("due_at"))),
        Attempt = r.GetInt32(r.GetOrdinal("attempt")),
        Kind = r.GetString(r.GetOrdinal("kind")),
        Reason = r.GetString(r.GetOrdinal("reason")),
        ProviderSaid = r.GetInt64(r.GetOrdinal("provider_said")) != 0,
        State = r.GetString(r.GetOrdinal("state")),
        CreatedAt = Parse(r.GetString(r.GetOrdinal("created_at"))),
    };
}
