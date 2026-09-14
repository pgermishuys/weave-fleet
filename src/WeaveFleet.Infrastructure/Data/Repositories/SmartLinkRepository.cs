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

    public async Task<bool> SetRelationshipAsync(string id, string relationship)
    {
        using var conn = _connectionFactory.CreateConnection();
        var affected = await conn.ExecuteNonQueryAsync(
            """
            UPDATE smart_links
            SET relationship = @Relationship, updated_at = @UpdatedAt
            WHERE id = @Id AND user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", id);
                cmd.AddParameter("Relationship", relationship);
                cmd.AddParameter("UpdatedAt", DateTime.UtcNow.ToString("O"));
                cmd.AddParameter("UserId", _userContext.UserId);
            });
        return affected > 0;
    }

    public async Task MarkSessionDueAsync(string sessionId)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE smart_links
            SET last_checked_at = NULL
            WHERE session_id = @SessionId AND user_id = @UserId AND is_dismissed = 0
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", _userContext.UserId);
            });
    }

    public async Task<SmartLink?> InsertDetectedAsync(SmartLink link, bool restoreDismissed, CancellationToken ct)
    {
        using var conn = _connectionFactory.CreateConnection();

        // The same pull request can arrive as /pull/N, /issues/N or owner/repo#N, so match on the resource.
        var existing = (await conn.QueryAsync(
            """
            SELECT * FROM smart_links
            WHERE session_id = @SessionId AND user_id = @UserId
              AND (url = @Url OR (resource_id <> '' AND resource_id = @ResourceId COLLATE NOCASE))
            LIMIT 1
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", link.SessionId);
                cmd.AddParameter("UserId", link.UserId);
                cmd.AddParameter("Url", link.Url);
                cmd.AddParameter("ResourceId", link.ResourceId);
            },
            MapSmartLink,
            ct).ConfigureAwait(false)).FirstOrDefault();

        var now = DateTime.UtcNow.ToString("O");

        if (existing is null)
        {
            var inserted = await conn.ExecuteNonQueryAsync(
                """
                INSERT INTO smart_links (
                    id, session_id, url, provider_id, resource_type, resource_id,
                    title, status, status_label, metadata_json, is_dismissed, is_terminal,
                    created_at, updated_at, user_id, relationship, enrichment_status, last_checked_at)
                SELECT
                    @Id, @SessionId, @Url, @ProviderId, @ResourceType, @ResourceId,
                    @Title, '', '', NULL, 0, 0,
                    @Now, @Now, @UserId, @Relationship, @EnrichmentStatus, NULL
                FROM sessions s
                WHERE s.id = @SessionId AND s.user_id = @UserId
                ON CONFLICT (session_id, url, user_id) DO NOTHING
                """,
                cmd =>
                {
                    cmd.AddParameter("Id", link.Id);
                    cmd.AddParameter("SessionId", link.SessionId);
                    cmd.AddParameter("Url", link.Url);
                    cmd.AddParameter("ProviderId", link.ProviderId);
                    cmd.AddParameter("ResourceType", link.ResourceType);
                    cmd.AddParameter("ResourceId", link.ResourceId);
                    cmd.AddParameter("Title", link.Title);
                    cmd.AddParameter("Now", now);
                    cmd.AddParameter("UserId", link.UserId);
                    cmd.AddParameter("Relationship", link.Relationship);
                    cmd.AddParameter("EnrichmentStatus", SmartLinkEnrichmentStatuses.Pending);
                },
                ct).ConfigureAwait(false);

            if (inserted == 0)
                return null;

            link.CreatedAt = now;
            link.UpdatedAt = now;
            link.EnrichmentStatus = SmartLinkEnrichmentStatuses.Pending;
            return link;
        }

        var upgrade = SmartLinkRelationships.Rank(link.Relationship) > SmartLinkRelationships.Rank(existing.Relationship);
        var restore = restoreDismissed && existing.IsDismissed;
        if (!upgrade && !restore)
            return null;

        existing.Relationship = upgrade ? link.Relationship : existing.Relationship;
        existing.IsDismissed = !restore && existing.IsDismissed;
        existing.UpdatedAt = now;

        await conn.ExecuteNonQueryAsync(
            """
            UPDATE smart_links
            SET relationship = @Relationship, is_dismissed = @IsDismissed, updated_at = @UpdatedAt
            WHERE id = @Id
            """,
            cmd =>
            {
                cmd.AddParameter("Id", existing.Id);
                cmd.AddParameter("Relationship", existing.Relationship);
                cmd.AddParameter("IsDismissed", existing.IsDismissed);
                cmd.AddParameter("UpdatedAt", now);
            },
            ct).ConfigureAwait(false);

        return existing;
    }

    public async Task<int> InsertMissingSourceLinksAsync(string? sessionId, CancellationToken ct)
    {
        using var conn = _connectionFactory.CreateConnection();
        const string target = "CASE u.action_id WHEN 'start-session' THEN 'origin' ELSE 'pinned' END";
        return await conn.ExecuteNonQueryAsync(
            $"""
            INSERT INTO smart_links (
                id, session_id, url, provider_id, resource_type, resource_id,
                title, status, status_label, metadata_json, is_dismissed, is_terminal,
                created_at, updated_at, user_id, relationship, enrichment_status, last_checked_at)
            SELECT
                lower(hex(randomblob(16))), u.session_id, u.resource_url, 'github',
                CASE u.source_type WHEN 'github-pull-request' THEN 'pull_request' ELSE 'issue' END,
                replace(replace(substr(u.resource_url, 20), '/issues/', '#'), '/pull/', '#'),
                COALESCE(u.title, ''), '', '', NULL, 0, 0,
                @Now, @Now, s.user_id, {target}, 'pending', NULL
            FROM session_source_usages u
            INNER JOIN sessions s ON s.id = u.session_id
            WHERE u.provider_id = 'builtin.github'
              AND u.resource_url LIKE 'https://github.com/%'
              AND s.retention_status = 'active'
              AND (u.session_id = @SessionId OR (@SessionId IS NULL AND s.lifecycle_status = 'running'))
              AND NOT EXISTS (
                  SELECT 1 FROM smart_links sl
                  WHERE sl.session_id = u.session_id AND sl.url = u.resource_url
                    AND {Rank("sl.relationship")} >= {Rank(target)})
            ON CONFLICT (session_id, url, user_id) DO UPDATE SET
                relationship = excluded.relationship,
                updated_at   = excluded.updated_at
            WHERE {Rank("excluded.relationship")} > {Rank("smart_links.relationship")}
            """,
            cmd =>
            {
                cmd.AddParameter("Now", DateTime.UtcNow.ToString("O"));
                cmd.AddParameter("SessionId", sessionId);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>SQL mirror of <see cref="SmartLinkRelationships.Rank"/>.</summary>
    private static string Rank(string expression) =>
        $"CASE {expression} WHEN 'origin' THEN 3 WHEN 'own' THEN 2 WHEN 'pinned' THEN 1 ELSE 0 END";

    public async Task<IReadOnlyList<SmartLink>> ListDueForEnrichmentAsync(
        string checkedBefore,
        string quietCheckedBefore,
        IReadOnlyCollection<string> busySessionIds,
        int limit,
        CancellationToken ct)
    {
        var busy = busySessionIds.ToList();
        var isBusy = busy.Count == 0
            ? "0"
            : $"sl.session_id IN ({string.Join(", ", busy.Select((_, i) => $"@Busy{i}"))})";

        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            $$"""
            SELECT sl.*
            FROM smart_links sl
            INNER JOIN sessions s ON s.id = sl.session_id
            WHERE sl.is_dismissed = 0
              AND s.retention_status = 'active'
              AND (
                    sl.enrichment_status IN ('pending', 'not_connected')
                 OR sl.last_checked_at IS NULL
                 OR (sl.is_terminal = 0 AND s.lifecycle_status = 'running' AND (
                        sl.last_checked_at < @QuietCheckedBefore
                     OR (sl.last_checked_at < @CheckedBefore AND {{isBusy}})))
              )
            ORDER BY
                CASE sl.enrichment_status WHEN 'pending' THEN 0 WHEN 'not_connected' THEN 2 ELSE 1 END,
                sl.last_checked_at IS NOT NULL,
                sl.last_checked_at
            LIMIT @Limit
            """,
            cmd =>
            {
                cmd.AddParameter("CheckedBefore", checkedBefore);
                cmd.AddParameter("QuietCheckedBefore", quietCheckedBefore);
                for (var i = 0; i < busy.Count; i++)
                    cmd.AddParameter($"Busy{i}", busy[i]);
                cmd.AddParameter("Limit", limit);
            },
            MapSmartLink,
            ct).ConfigureAwait(false);
    }

    public async Task UpdateEnrichmentAsync(SmartLink link, CancellationToken ct)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE smart_links
            SET resource_type     = @ResourceType,
                resource_id       = @ResourceId,
                title             = @Title,
                status            = @Status,
                status_label      = @StatusLabel,
                metadata_json     = @MetadataJson,
                is_terminal       = @IsTerminal,
                relationship      = @Relationship,
                enrichment_status = @EnrichmentStatus,
                last_checked_at   = @LastCheckedAt,
                updated_at        = @UpdatedAt
            WHERE id = @Id
            """,
            cmd =>
            {
                cmd.AddParameter("Id", link.Id);
                cmd.AddParameter("ResourceType", link.ResourceType);
                cmd.AddParameter("ResourceId", link.ResourceId);
                cmd.AddParameter("Title", link.Title);
                cmd.AddParameter("Status", link.Status);
                cmd.AddParameter("StatusLabel", link.StatusLabel);
                cmd.AddParameter("MetadataJson", link.MetadataJson);
                cmd.AddParameter("IsTerminal", link.IsTerminal);
                cmd.AddParameter("Relationship", link.Relationship);
                cmd.AddParameter("EnrichmentStatus", link.EnrichmentStatus);
                cmd.AddParameter("LastCheckedAt", link.LastCheckedAt);
                cmd.AddParameter("UpdatedAt", link.UpdatedAt);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SmartLinkBranchTarget>> ListBranchTargetsAsync(CancellationToken ct)
    {
        using var conn = _connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT s.id AS session_id, s.user_id, w.branch, w.directory, w.source_directory
            FROM sessions s
            INNER JOIN workspaces w ON w.id = s.workspace_id
            WHERE s.lifecycle_status = 'running'
              AND s.retention_status = 'active'
              AND w.branch IS NOT NULL AND w.branch <> ''
            """,
            cmd => { },
            r => new SmartLinkBranchTarget(
                r.GetString(r.GetOrdinal("session_id")),
                r.GetString(r.GetOrdinal("user_id")),
                r.GetString(r.GetOrdinal("branch")),
                r.GetString(r.GetOrdinal("directory")),
                r.GetNullableString(r.GetOrdinal("source_directory"))),
            ct).ConfigureAwait(false);
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
            Relationship = r.GetString(r.GetOrdinal("relationship")),
            EnrichmentStatus = r.GetString(r.GetOrdinal("enrichment_status")),
            LastCheckedAt = r.GetNullableString(r.GetOrdinal("last_checked_at")),
        };
    }
}
