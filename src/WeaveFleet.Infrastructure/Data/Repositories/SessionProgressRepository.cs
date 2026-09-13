using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class SessionProgressRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : ISessionProgressRepository
{
    public async Task<SessionProgress?> GetAsync(string sessionId, CancellationToken ct)
        => await GetForOwnerAsync(sessionId, userContext.UserId, ct).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, SessionProgress>> GetManyAsync(IReadOnlyCollection<string> sessionIds, CancellationToken ct)
    {
        if (sessionIds.Count == 0)
            return new Dictionary<string, SessionProgress>(StringComparer.Ordinal);

        using var conn = connectionFactory.CreateConnection();
        await using var cmd = ((DbConnection)conn).CreateCommand();

        var sql = new StringBuilder("SELECT * FROM session_progress WHERE user_id = @UserId AND session_id ");
        SqlInExpander.AppendInClause(sql, cmd, "SessionId", sessionIds.ToList());
        cmd.AddParameter("UserId", userContext.UserId);
        cmd.CommandText = sql.ToString();

        var results = new Dictionary<string, SessionProgress>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var progress = Map(reader);
            results[progress.SessionId] = progress;
        }

        return results;
    }

    public async Task<SessionProgress?> GetForOwnerAsync(string sessionId, string userId, CancellationToken ct)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync(
            "SELECT * FROM session_progress WHERE session_id = @SessionId AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userId);
            },
            Map,
            ct).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public async Task<bool> UpsertAsync(SessionProgress progress, CancellationToken ct)
    {
        var detail = new SessionProgressDetailJson { Todos = progress.Todos, Plans = progress.Plans };

        using var conn = connectionFactory.CreateConnection();
        var affected = await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO session_progress (session_id, user_id, kind, done, total, current, detail_json, updated_at)
            SELECT @SessionId, @UserId, @Kind, @Done, @Total, @Current, @DetailJson, @UpdatedAt
            FROM sessions s
            WHERE s.id = @SessionId AND s.user_id = @UserId
            ON CONFLICT (session_id) DO UPDATE SET
                kind = excluded.kind,
                done = excluded.done,
                total = excluded.total,
                current = excluded.current,
                detail_json = excluded.detail_json,
                updated_at = excluded.updated_at
            WHERE session_progress.user_id = excluded.user_id
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", progress.SessionId);
                cmd.AddParameter("UserId", progress.UserId);
                cmd.AddParameter("Kind", progress.Kind);
                cmd.AddParameter("Done", progress.Done);
                cmd.AddParameter("Total", progress.Total);
                cmd.AddParameter("Current", progress.Current);
                cmd.AddParameter("DetailJson", JsonSerializer.Serialize(detail, InfrastructureJsonContext.Default.SessionProgressDetailJson));
                cmd.AddParameter("UpdatedAt", progress.UpdatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            },
            ct).ConfigureAwait(false);
        return affected > 0;
    }

    private static SessionProgress Map(DbDataReader r)
    {
        var detailJson = r.GetString(r.GetOrdinal("detail_json"));
        SessionProgressDetailJson? detail;
        try
        {
            detail = JsonSerializer.Deserialize(detailJson, InfrastructureJsonContext.Default.SessionProgressDetailJson);
        }
        catch (JsonException)
        {
            detail = null;
        }

        return new SessionProgress
        {
            SessionId = r.GetString(r.GetOrdinal("session_id")),
            UserId = r.GetString(r.GetOrdinal("user_id")),
            Kind = r.GetString(r.GetOrdinal("kind")),
            Done = r.GetInt32(r.GetOrdinal("done")),
            Total = r.GetInt32(r.GetOrdinal("total")),
            Current = r.GetNullableString(r.GetOrdinal("current")),
            Todos = detail?.Todos ?? [],
            Plans = detail?.Plans ?? [],
            UpdatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("updated_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        };
    }
}

/// <summary>The part of a session's progress stored as JSON in <c>session_progress.detail_json</c>.</summary>
internal sealed record SessionProgressDetailJson
{
    public IReadOnlyList<TodoEntry> Todos { get; init; } = [];
    public IReadOnlyList<TrackedPlan> Plans { get; init; } = [];
}
