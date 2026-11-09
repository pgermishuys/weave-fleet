using System.Globalization;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>The <c>turn_file_stats</c> table (migration 073).</summary>
public sealed class TurnFileStatsRepository(IDbConnectionFactory connectionFactory, IUserContext userContext) : ITurnFileStatsRepository
{
    /// <summary>Snapshots kept per session; older turns' cards show no counts.</summary>
    internal const int MaxSnapshots = 200;

    public async Task AddAsync(string sessionId, DateTimeOffset endedAt, IReadOnlyList<TurnFileStat> files)
    {
        var at = Format(endedAt);
        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        // The marker row says the snapshot was taken even when no file changed.
        foreach (var file in files.Prepend(new TurnFileStat(string.Empty, 0, 0)))
        {
            await conn.ExecuteNonQueryAsync(
                """
                INSERT OR REPLACE INTO turn_file_stats (session_id, ended_at, file, additions, deletions)
                SELECT @SessionId, @At, @File, @Additions, @Deletions
                FROM sessions WHERE id = @SessionId AND user_id = @UserId
                """,
                cmd =>
                {
                    cmd.AddParameter("SessionId", sessionId);
                    cmd.AddParameter("UserId", userContext.UserId);
                    cmd.AddParameter("At", at);
                    cmd.AddParameter("File", file.File);
                    cmd.AddParameter("Additions", file.Additions);
                    cmd.AddParameter("Deletions", file.Deletions);
                },
                tx);
        }

        await conn.ExecuteNonQueryAsync(
            $"""
            DELETE FROM turn_file_stats WHERE session_id = @SessionId AND ended_at NOT IN (
              SELECT DISTINCT ended_at FROM turn_file_stats WHERE session_id = @SessionId ORDER BY ended_at DESC LIMIT {MaxSnapshots})
            """,
            cmd => cmd.AddParameter("SessionId", sessionId),
            tx);
        tx.Commit();
    }

    public async Task<IReadOnlyList<TurnFileStats>> ListAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync(
            """
            SELECT t.ended_at, t.file, t.additions, t.deletions FROM turn_file_stats t
            JOIN sessions s ON s.id = t.session_id
            WHERE t.session_id = @SessionId AND s.user_id = @UserId
            ORDER BY t.ended_at, t.file
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            r => (At: r.GetString(0), File: r.GetString(1), Additions: r.GetInt32(2), Deletions: r.GetInt32(3)));

        return [.. rows
            .GroupBy(row => row.At)
            .Select(group => new TurnFileStats(
                DateTimeOffset.Parse(group.Key, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                [.. group.Where(row => row.File.Length > 0).Select(row => new TurnFileStat(row.File, row.Additions, row.Deletions))]))];
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
