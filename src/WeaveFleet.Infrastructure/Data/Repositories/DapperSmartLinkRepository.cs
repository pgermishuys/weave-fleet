using Dapper;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class DapperSmartLinkRepository : ISmartLinkRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IUserContext _userContext;

    public DapperSmartLinkRepository(IDbConnectionFactory connectionFactory, IUserContext userContext)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
    }

    public async Task<IReadOnlyList<SmartLink>> ListBySessionIdAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<SmartLink>(
            """
            SELECT sl.*
            FROM smart_links sl
            INNER JOIN sessions s ON s.id = sl.session_id
            WHERE sl.session_id = @SessionId AND sl.user_id = @UserId
            ORDER BY sl.created_at ASC
            """,
            new { SessionId = sessionId, UserId = _userContext.UserId });
        return results.AsList();
    }

    public async Task<IReadOnlyList<SmartLink>> ListActiveBySessionIdAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var results = await conn.QueryAsync<SmartLink>(
            """
            SELECT sl.*
            FROM smart_links sl
            INNER JOIN sessions s ON s.id = sl.session_id
            WHERE sl.session_id = @SessionId AND sl.user_id = @UserId AND sl.is_dismissed = 0
            ORDER BY sl.created_at ASC
            """,
            new { SessionId = sessionId, UserId = _userContext.UserId });
        return results.AsList();
    }

    public async Task<SmartLink?> GetBySessionIdAndUrlAsync(string sessionId, string url)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<SmartLink>(
            """
            SELECT sl.*
            FROM smart_links sl
            INNER JOIN sessions s ON s.id = sl.session_id
            WHERE sl.session_id = @SessionId AND sl.url = @Url AND sl.user_id = @UserId
            """,
            new { SessionId = sessionId, Url = url, UserId = _userContext.UserId });
    }

    public async Task UpsertAsync(SmartLink smartLink)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO smart_links (
                id, session_id, url, provider_id, resource_type, resource_id,
                title, status, status_label, metadata_json, is_dismissed, is_terminal,
                created_at, updated_at, user_id)
            SELECT
                @Id, @SessionId, @Url, @ProviderId, @ResourceType, @ResourceId,
                @Title, @Status, @StatusLabel, @MetadataJson, @IsDismissed, @IsTerminal,
                @CreatedAt, @UpdatedAt, @UserId
            FROM sessions s
            WHERE s.id = @SessionId AND s.user_id = @UserId
            ON CONFLICT (session_id, url, user_id) DO UPDATE SET
                provider_id   = excluded.provider_id,
                resource_type = excluded.resource_type,
                resource_id   = excluded.resource_id,
                title         = excluded.title,
                status        = excluded.status,
                status_label  = excluded.status_label,
                metadata_json = excluded.metadata_json,
                is_terminal   = excluded.is_terminal,
                updated_at    = excluded.updated_at
            WHERE smart_links.is_dismissed = 0
            """,
            new
            {
                smartLink.Id,
                smartLink.SessionId,
                smartLink.Url,
                smartLink.ProviderId,
                smartLink.ResourceType,
                smartLink.ResourceId,
                smartLink.Title,
                smartLink.Status,
                smartLink.StatusLabel,
                smartLink.MetadataJson,
                smartLink.IsDismissed,
                smartLink.IsTerminal,
                smartLink.CreatedAt,
                smartLink.UpdatedAt,
                UserId = _userContext.UserId
            });
    }

    public async Task DismissAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteAsync(
            """
            UPDATE smart_links
            SET is_dismissed = 1, updated_at = @UpdatedAt
            WHERE id = @Id AND user_id = @UserId
            """,
            new
            {
                Id = id,
                UpdatedAt = DateTime.UtcNow.ToString("O"),
                UserId = _userContext.UserId
            });
    }
}
