using System.Data.Common;
using System.Text;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperSessionSourceUsageRepository : ISessionSourceUsageRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    private readonly IUserContext _userContext;

    public DapperSessionSourceUsageRepository(IDbConnectionFactory connectionFactory, IUserContext userContext)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
    }

    public async Task InsertAsync(SessionSourceUsage usage)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO session_source_usages (
                id, session_id, workspace_id, provider_id, source_type, action_id,
                resource_id, resource_url, title, summary, created_at)
            SELECT
                @Id, @SessionId, @WorkspaceId, @ProviderId, @SourceType, @ActionId,
                @ResourceId, @ResourceUrl, @Title, @Summary, @CreatedAt
            FROM sessions session_row
            WHERE session_row.id = @SessionId
              AND session_row.user_id = @UserId
              AND (
                  @WorkspaceId IS NULL
                  OR EXISTS (
                      SELECT 1 FROM workspaces workspace_row
                      WHERE workspace_row.id = @WorkspaceId AND workspace_row.user_id = @UserId))
            """,
            cmd =>
            {
                cmd.AddParameter("Id", usage.Id);
                cmd.AddParameter("SessionId", usage.SessionId);
                cmd.AddParameter("WorkspaceId", usage.WorkspaceId);
                cmd.AddParameter("ProviderId", usage.ProviderId);
                cmd.AddParameter("SourceType", usage.SourceType);
                cmd.AddParameter("ActionId", usage.ActionId);
                cmd.AddParameter("ResourceId", usage.ResourceId);
                cmd.AddParameter("ResourceUrl", usage.ResourceUrl);
                cmd.AddParameter("Title", usage.Title);
                cmd.AddParameter("Summary", usage.Summary);
                cmd.AddParameter("CreatedAt", usage.CreatedAt);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task<SessionSourceUsage?> GetPrimaryBySessionIdAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            """
            SELECT ssu.*
            FROM session_source_usages ssu
            INNER JOIN sessions session_row ON session_row.id = ssu.session_id
            WHERE ssu.session_id = @SessionId
              AND ssu.action_id = @ActionId
              AND session_row.user_id = @UserId
            ORDER BY ssu.created_at ASC
            LIMIT 1
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("ActionId", SessionSourceActions.StartSession);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadSessionSourceUsage);
    }

    public async Task<IReadOnlyDictionary<string, SessionSourceUsage>> GetPrimaryBySessionIdsAsync(IReadOnlyCollection<string> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            return new Dictionary<string, SessionSourceUsage>();
        }

        using var conn = _connectionFactory.CreateConnection();
        var dbConn = (DbConnection)conn;
        await using var cmd = dbConn.CreateCommand();

        var sql = new StringBuilder(
            """
            SELECT ranked.*
            FROM (
                SELECT
                    ssu.*,
                    ROW_NUMBER() OVER (
                        PARTITION BY ssu.session_id
                        ORDER BY ssu.created_at ASC)
                    AS row_num
                FROM session_source_usages ssu
                INNER JOIN sessions session_row ON session_row.id = ssu.session_id
                WHERE ssu.session_id 
            """);
        SqlInExpander.AppendInClause(sql, cmd, "SessionId", sessionIds.ToList());
        sql.Append("""

                  AND ssu.action_id = @ActionId
                  AND session_row.user_id = @UserId
            ) ranked
            WHERE ranked.row_num = 1
            """);
        cmd.AddParameter("ActionId", SessionSourceActions.StartSession);
        cmd.AddParameter("UserId", _userContext.UserId);

        cmd.CommandText = sql.ToString();
        await using var reader = await cmd.ExecuteReaderAsync();
        var results = new Dictionary<string, SessionSourceUsage>();
        while (await reader.ReadAsync())
        {
            var usage = ReadSessionSourceUsage(reader);
            results[usage.SessionId] = usage;
        }
        return results;
    }

    public async Task<IReadOnlyList<SessionSourceUsage>> ListBySessionIdAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT ssu.*
            FROM session_source_usages ssu
            INNER JOIN sessions session_row ON session_row.id = ssu.session_id
            WHERE ssu.session_id = @SessionId AND session_row.user_id = @UserId
            ORDER BY ssu.created_at DESC
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            },
            ReadSessionSourceUsage);
    }

    private static SessionSourceUsage ReadSessionSourceUsage(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        WorkspaceId = r.GetNullableString(r.GetOrdinal("workspace_id")),
        ProviderId = r.GetString(r.GetOrdinal("provider_id")),
        SourceType = r.GetString(r.GetOrdinal("source_type")),
        ActionId = r.GetString(r.GetOrdinal("action_id")),
        ResourceId = r.GetNullableString(r.GetOrdinal("resource_id")),
        ResourceUrl = r.GetNullableString(r.GetOrdinal("resource_url")),
        Title = r.GetNullableString(r.GetOrdinal("title")),
        Summary = r.GetNullableString(r.GetOrdinal("summary")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
    };
}
