using System.Data;
using System.Data.Common;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class CanvasRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : ICanvasRepository
{
    public async Task<IReadOnlyList<Canvas>> ListBySessionIdAsync(string sessionId, bool includeClosed = false)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT * FROM canvases
            WHERE session_id = @SessionId
              AND user_id = @UserId
              AND (@IncludeClosed = 1 OR closed_at IS NULL)
            ORDER BY created_at ASC, id ASC
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("IncludeClosed", includeClosed);
            },
            ReadCanvas);
    }

    public async Task<Canvas?> GetByIdAsync(string sessionId, string canvasId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT * FROM canvases WHERE id = @Id AND session_id = @SessionId AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("Id", canvasId);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadCanvas);
    }

    public async Task<Canvas?> GetByTitleAsync(string sessionId, string title)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            """
            SELECT * FROM canvases
            WHERE session_id = @SessionId AND title = @Title AND user_id = @UserId
            ORDER BY updated_at DESC, id DESC
            LIMIT 1
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("Title", title);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadCanvas);
    }

    public async Task<bool> InsertAsync(Canvas canvas, CanvasRevision revision)
    {
        EnsureRevisionMatches(canvas, revision);

        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var rows = await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO canvases (id, session_id, user_id, kind, title, state_json, version,
                agent_seen_version, created_at, updated_at, closed_at)
            SELECT @Id, @SessionId, @UserId, @Kind, @Title, @StateJson, @Version,
                @AgentSeenVersion, @CreatedAt, @UpdatedAt, @ClosedAt
            FROM sessions session_row
            WHERE session_row.id = @SessionId AND session_row.user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", canvas.Id);
                cmd.AddParameter("SessionId", canvas.SessionId);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("Kind", canvas.Kind);
                cmd.AddParameter("Title", canvas.Title);
                cmd.AddParameter("StateJson", canvas.StateJson);
                cmd.AddParameter("Version", canvas.Version);
                cmd.AddParameter("AgentSeenVersion", canvas.AgentSeenVersion);
                cmd.AddParameter("CreatedAt", canvas.CreatedAt);
                cmd.AddParameter("UpdatedAt", canvas.UpdatedAt);
                cmd.AddParameter("ClosedAt", canvas.ClosedAt);
            },
            tx);

        if (rows == 0)
        {
            tx.Rollback();
            return false;
        }

        await InsertRevisionAsync(conn, tx, revision);
        tx.Commit();
        return true;
    }

    public async Task<bool> TryUpdateAsync(Canvas canvas, int expectedVersion, CanvasRevision revision)
    {
        if (canvas.Version != expectedVersion + 1)
            throw new ArgumentException($"Canvas version must be {expectedVersion + 1}, was {canvas.Version}.", nameof(canvas));
        EnsureRevisionMatches(canvas, revision);

        using var conn = connectionFactory.CreateConnection();
        using var tx = conn.BeginTransaction();

        // Only the state moves with a version. Kind and title are fixed at creation, and
        // closing or reopening goes through SetClosedAtAsync so a stale write can't undo a close.
        var rows = await conn.ExecuteNonQueryAsync(
            """
            UPDATE canvases
            SET state_json = @StateJson,
                version = @Version,
                agent_seen_version = MAX(agent_seen_version, @AgentSeenVersion),
                updated_at = @UpdatedAt
            WHERE id = @Id
              AND session_id = @SessionId
              AND user_id = @UserId
              AND version = @ExpectedVersion
            """,
            cmd =>
            {
                cmd.AddParameter("Id", canvas.Id);
                cmd.AddParameter("SessionId", canvas.SessionId);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("StateJson", canvas.StateJson);
                cmd.AddParameter("Version", canvas.Version);
                cmd.AddParameter("AgentSeenVersion", canvas.AgentSeenVersion);
                cmd.AddParameter("UpdatedAt", canvas.UpdatedAt);
                cmd.AddParameter("ExpectedVersion", expectedVersion);
            },
            tx);

        if (rows == 0)
        {
            tx.Rollback();
            return false;
        }

        await InsertRevisionAsync(conn, tx, revision);
        tx.Commit();
        return true;
    }

    public async Task MarkAgentSeenAsync(string sessionId, string canvasId, int version)
    {
        using var conn = connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            UPDATE canvases
            SET agent_seen_version = MAX(agent_seen_version, MIN(@Version, version))
            WHERE id = @Id AND session_id = @SessionId AND user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", canvasId);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("Version", version);
            });
    }

    public async Task<bool> SetClosedAtAsync(string sessionId, string canvasId, string? closedAt)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteNonQueryAsync(
            """
            UPDATE canvases
            SET closed_at = @ClosedAt
            WHERE id = @Id AND session_id = @SessionId AND user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", canvasId);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
                cmd.AddParameter("ClosedAt", closedAt);
            });
        return rows > 0;
    }

    public async Task<IReadOnlyList<CanvasRevision>> ListRevisionsAsync(string canvasId, int afterVersion)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            """
            SELECT revision.*
            FROM canvas_revisions revision
            INNER JOIN canvases canvas ON canvas.id = revision.canvas_id
            WHERE revision.canvas_id = @CanvasId
              AND revision.version > @AfterVersion
              AND canvas.user_id = @UserId
            ORDER BY revision.version ASC
            """,
            cmd =>
            {
                cmd.AddParameter("CanvasId", canvasId);
                cmd.AddParameter("AfterVersion", afterVersion);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            ReadRevision);
    }

    private static async Task InsertRevisionAsync(IDbConnection conn, IDbTransaction tx, CanvasRevision revision)
    {
        await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO canvas_revisions (canvas_id, version, actor, ops_json, created_at)
            VALUES (@CanvasId, @Version, @Actor, @OpsJson, @CreatedAt)
            """,
            cmd =>
            {
                cmd.AddParameter("CanvasId", revision.CanvasId);
                cmd.AddParameter("Version", revision.Version);
                cmd.AddParameter("Actor", revision.Actor);
                cmd.AddParameter("OpsJson", revision.OpsJson);
                cmd.AddParameter("CreatedAt", revision.CreatedAt);
            },
            tx);
    }

    private static void EnsureRevisionMatches(Canvas canvas, CanvasRevision revision)
    {
        if (!string.Equals(revision.CanvasId, canvas.Id, StringComparison.Ordinal))
            throw new ArgumentException("Revision belongs to a different canvas.", nameof(revision));
        if (revision.Version != canvas.Version)
            throw new ArgumentException($"Revision version must be {canvas.Version}, was {revision.Version}.", nameof(revision));
    }

    private static Canvas ReadCanvas(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        Kind = r.GetString(r.GetOrdinal("kind")),
        Title = r.GetString(r.GetOrdinal("title")),
        StateJson = r.GetString(r.GetOrdinal("state_json")),
        Version = (int)r.GetInt64(r.GetOrdinal("version")),
        AgentSeenVersion = (int)r.GetInt64(r.GetOrdinal("agent_seen_version")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetString(r.GetOrdinal("updated_at")),
        ClosedAt = r.GetNullableString(r.GetOrdinal("closed_at")),
    };

    private static CanvasRevision ReadRevision(DbDataReader r) => new()
    {
        CanvasId = r.GetString(r.GetOrdinal("canvas_id")),
        Version = (int)r.GetInt64(r.GetOrdinal("version")),
        Actor = r.GetString(r.GetOrdinal("actor")),
        OpsJson = r.GetString(r.GetOrdinal("ops_json")),
        CreatedAt = r.GetString(r.GetOrdinal("created_at")),
    };
}
