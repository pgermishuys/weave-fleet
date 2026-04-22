using Dapper;
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
        await conn.ExecuteAsync(
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
            new
            {
                usage.Id,
                usage.SessionId,
                usage.WorkspaceId,
                usage.ProviderId,
                usage.SourceType,
                usage.ActionId,
                usage.ResourceId,
                usage.ResourceUrl,
                usage.Title,
                usage.Summary,
                usage.CreatedAt,
                UserId = _userContext.UserId
             });
    }

    public async Task<SessionSourceUsage?> GetPrimaryBySessionIdAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<SessionSourceUsage>(
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
            new
            {
                SessionId = sessionId,
                ActionId = SessionSourceActions.StartSession,
                UserId = _userContext.UserId
            });
    }

    public async Task<IReadOnlyDictionary<string, SessionSourceUsage>> GetPrimaryBySessionIdsAsync(IReadOnlyCollection<string> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            return new Dictionary<string, SessionSourceUsage>();
        }

        using var conn = _connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<SessionSourceUsage>(
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
                WHERE ssu.session_id IN @SessionIds
                  AND ssu.action_id = @ActionId
                  AND session_row.user_id = @UserId
            ) ranked
            WHERE ranked.row_num = 1
            """,
            new
            {
                SessionIds = sessionIds,
                ActionId = SessionSourceActions.StartSession,
                UserId = _userContext.UserId
            });

        return results.ToDictionary(usage => usage.SessionId, usage => usage);
    }

    public async Task<IReadOnlyList<SessionSourceUsage>> ListBySessionIdAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<SessionSourceUsage>(
            """
            SELECT ssu.*
            FROM session_source_usages ssu
            INNER JOIN sessions session_row ON session_row.id = ssu.session_id
            WHERE ssu.session_id = @SessionId AND session_row.user_id = @UserId
            ORDER BY ssu.created_at DESC
            """,
            new { SessionId = sessionId, UserId = _userContext.UserId });
        return results.AsList();
    }
}
