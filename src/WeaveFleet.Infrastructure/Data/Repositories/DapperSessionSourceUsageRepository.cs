using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperSessionSourceUsageRepository(IDbConnectionFactory connectionFactory) : ISessionSourceUsageRepository
{
    public async Task InsertAsync(SessionSourceUsage usage)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO session_source_usages (
                id, session_id, workspace_id, provider_id, source_type, action_id,
                resource_id, resource_url, title, summary, created_at)
            VALUES (
                @Id, @SessionId, @WorkspaceId, @ProviderId, @SourceType, @ActionId,
                @ResourceId, @ResourceUrl, @Title, @Summary, @CreatedAt)
            """,
            usage);
    }

    public async Task<IReadOnlyList<SessionSourceUsage>> ListBySessionIdAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<SessionSourceUsage>(
            "SELECT * FROM session_source_usages WHERE session_id = @SessionId ORDER BY created_at DESC",
            new { SessionId = sessionId });
        return results.AsList();
    }
}
