using System.Data.Common;
using System.Globalization;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>The <c>session_interruptions</c> table and the turn flag on <c>sessions</c> (migration 063).</summary>
public sealed class SessionInterruptionRepository(IDbConnectionFactory connectionFactory, IUserContext userContext) : ISessionInterruptionRepository
{
    private const string Columns = "session_id, user_id, at, waiting, last_call, changed_files";

    public async Task FlagTurnAsync(string sessionId, DateTimeOffset startedAt, bool waiting)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET turn_started_at = @At, turn_waiting = @Waiting WHERE id = @SessionId",
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("At", Format(startedAt));
                cmd.AddParameter("Waiting", waiting ? 1 : 0);
            });
    }

    public async Task SetWaitingAsync(string sessionId, bool waiting)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET turn_waiting = @Waiting WHERE id = @SessionId AND turn_started_at IS NOT NULL AND turn_waiting <> @Waiting",
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("Waiting", waiting ? 1 : 0);
            });
    }

    public async Task ClearTurnAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            "UPDATE sessions SET turn_started_at = NULL, turn_waiting = 0 WHERE id = @SessionId AND turn_started_at IS NOT NULL",
            cmd => cmd.AddParameter("SessionId", sessionId));
    }

    public async Task<IReadOnlyList<FlaggedTurn>> ListFlaggedAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT id, user_id, turn_started_at, turn_waiting FROM sessions
            WHERE turn_started_at IS NOT NULL AND retention_status <> 'archived'
            """,
            _ => { },
            r => new FlaggedTurn(
                r.GetString(0),
                r.GetString(1),
                Parse(r.GetString(2)),
                r.GetInt64(3) != 0));
    }

    public async Task<SessionInterruption?> GetAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"SELECT {Columns} FROM session_interruptions WHERE session_id = @SessionId AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            Read);
    }

    public async Task<IReadOnlyList<SessionInterruption>> ListForUserAsync()
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            $"SELECT {Columns} FROM session_interruptions WHERE user_id = @UserId",
            cmd => cmd.AddParameter("UserId", userContext.UserId),
            Read);
    }

    public async Task SaveAsync(SessionInterruption interruption)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO session_interruptions (session_id, user_id, at, waiting, last_call, changed_files)
            SELECT @SessionId, @UserId, @At, @Waiting, @LastCall, @ChangedFiles
            FROM sessions WHERE id = @SessionId AND user_id = @UserId
            ON CONFLICT (session_id) DO UPDATE SET
              at = excluded.at, waiting = excluded.waiting, last_call = excluded.last_call, changed_files = excluded.changed_files
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", interruption.SessionId);
                cmd.AddParameter("UserId", interruption.UserId);
                cmd.AddParameter("At", Format(interruption.At));
                cmd.AddParameter("Waiting", interruption.Waiting ? 1 : 0);
                cmd.AddParameter("LastCall", interruption.LastCall);
                cmd.AddParameter("ChangedFiles", interruption.ChangedFiles);
            });
    }

    public async Task<SessionInterruption?> RemoveAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"DELETE FROM session_interruptions WHERE session_id = @SessionId AND user_id = @UserId RETURNING {Columns}",
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

    private static SessionInterruption Read(DbDataReader r) => new()
    {
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        At = Parse(r.GetString(r.GetOrdinal("at"))),
        Waiting = r.GetInt64(r.GetOrdinal("waiting")) != 0,
        LastCall = r.IsDBNull(r.GetOrdinal("last_call")) ? null : r.GetString(r.GetOrdinal("last_call")),
        ChangedFiles = r.IsDBNull(r.GetOrdinal("changed_files")) ? null : r.GetInt32(r.GetOrdinal("changed_files")),
    };
}
