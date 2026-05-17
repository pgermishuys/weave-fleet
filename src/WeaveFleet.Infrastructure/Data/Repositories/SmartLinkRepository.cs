using System.Data;
using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class SmartLinkRepository : ISmartLinkRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IUserContext _userContext;

    public SmartLinkRepository(IDbConnectionFactory connectionFactory, IUserContext userContext)
    {
        _connectionFactory = connectionFactory;
        _userContext = userContext;
    }

    public async Task<IReadOnlyList<SmartLink>> ListBySessionIdAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT sl.*
            FROM smart_links sl
            INNER JOIN sessions s ON s.id = sl.session_id
            WHERE sl.session_id = @SessionId AND sl.user_id = @UserId
            ORDER BY sl.created_at ASC
            """,
            cmd => { cmd.AddParameter("SessionId", sessionId); cmd.AddParameter("UserId", _userContext.UserId); },
            MapSmartLink);
    }

    public async Task<IReadOnlyList<SmartLink>> ListActiveBySessionIdAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT sl.*
            FROM smart_links sl
            INNER JOIN sessions s ON s.id = sl.session_id
            WHERE sl.session_id = @SessionId AND sl.user_id = @UserId AND sl.is_dismissed = 0
            ORDER BY sl.created_at ASC
            """,
            cmd => { cmd.AddParameter("SessionId", sessionId); cmd.AddParameter("UserId", _userContext.UserId); },
            MapSmartLink);
    }

    public async Task<SmartLink?> GetBySessionIdAndUrlAsync(string sessionId, string url)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            """
            SELECT sl.*
            FROM smart_links sl
            INNER JOIN sessions s ON s.id = sl.session_id
            WHERE sl.session_id = @SessionId AND sl.url = @Url AND sl.user_id = @UserId
            """,
            cmd => { cmd.AddParameter("SessionId", sessionId); cmd.AddParameter("Url", url); cmd.AddParameter("UserId", _userContext.UserId); },
            MapSmartLink);
    }

    public async Task UpsertAsync(SmartLink smartLink)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("Id", smartLink.Id);
                cmd.AddParameter("SessionId", smartLink.SessionId);
                cmd.AddParameter("Url", smartLink.Url);
                cmd.AddParameter("ProviderId", smartLink.ProviderId);
                cmd.AddParameter("ResourceType", smartLink.ResourceType);
                cmd.AddParameter("ResourceId", smartLink.ResourceId);
                cmd.AddParameter("Title", smartLink.Title);
                cmd.AddParameter("Status", smartLink.Status);
                cmd.AddParameter("StatusLabel", smartLink.StatusLabel);
                cmd.AddParameter("MetadataJson", smartLink.MetadataJson);
                cmd.AddParameter("IsDismissed", smartLink.IsDismissed);
                cmd.AddParameter("IsTerminal", smartLink.IsTerminal);
                cmd.AddParameter("CreatedAt", smartLink.CreatedAt);
                cmd.AddParameter("UpdatedAt", smartLink.UpdatedAt);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task DismissAsync(string id)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE smart_links
            SET is_dismissed = 1, updated_at = @UpdatedAt
            WHERE id = @Id AND user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("UpdatedAt", DateTime.UtcNow.ToString("O"));
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task<IReadOnlyList<SmartLink>> ListNonTerminalPrLinksAsync(CancellationToken ct)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT sl.*
            FROM smart_links sl
            INNER JOIN sessions s ON s.id = sl.session_id
            WHERE sl.resource_type = 'pull_request'
              AND sl.is_terminal = 0
              AND sl.is_dismissed = 0
              AND s.lifecycle_status = 'running'
            ORDER BY sl.created_at ASC
            """,
            cmd => { },
            MapSmartLink,
            ct);
    }

    public async Task DeleteBySessionIdAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        await DeleteBySessionIdAsync(conn, null, sessionId);
    }

    public async Task DeleteBySessionIdAsync(IDbConnection connection, IDbTransaction? transaction, string sessionId)
    {
        await connection.ExecuteNonQueryAsync(
            "DELETE FROM smart_links WHERE session_id = @SessionId",
            cmd => cmd.AddParameter("SessionId", sessionId),
            transaction);
    }

    public async Task DeleteOrphanedAsync(CancellationToken ct)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            DELETE FROM smart_links
            WHERE NOT EXISTS (
                SELECT 1 FROM sessions s WHERE s.id = smart_links.session_id
            )
            """,
            cmd => { },
            ct);
    }

    public async Task UpdateMetadataAsync(string id, string metadataJson, CancellationToken ct)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE smart_links
            SET metadata_json = @MetadataJson, updated_at = @UpdatedAt
            WHERE id = @Id
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("MetadataJson", metadataJson);
                cmd.AddParameter("UpdatedAt", DateTime.UtcNow.ToString("O"));
            },
            ct);
    }

    private static SmartLink MapSmartLink(DbDataReader r)
    {
        var metadataJsonOrd = r.GetOrdinal("metadata_json");
        return new SmartLink
        {
            Id = r.GetString(r.GetOrdinal("id")),
            SessionId = r.GetString(r.GetOrdinal("session_id")),
            Url = r.GetString(r.GetOrdinal("url")),
            ProviderId = r.GetString(r.GetOrdinal("provider_id")),
            ResourceType = r.GetString(r.GetOrdinal("resource_type")),
            ResourceId = r.GetString(r.GetOrdinal("resource_id")),
            Title = r.GetString(r.GetOrdinal("title")),
            Status = r.GetString(r.GetOrdinal("status")),
            StatusLabel = r.GetString(r.GetOrdinal("status_label")),
            MetadataJson = r.GetNullableString(metadataJsonOrd),
            IsDismissed = r.GetBoolean(r.GetOrdinal("is_dismissed")),
            IsTerminal = r.GetBoolean(r.GetOrdinal("is_terminal")),
            CreatedAt = r.GetString(r.GetOrdinal("created_at")),
            UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
            UserId = r.GetString(r.GetOrdinal("user_id")),
        };
    }
}
