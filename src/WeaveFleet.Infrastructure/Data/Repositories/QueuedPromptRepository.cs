using System.Data.Common;
using System.Globalization;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>The <c>queued_prompts</c> table (migration 044), scoped to the current user's sessions.</summary>
public sealed class QueuedPromptRepository(IDbConnectionFactory connectionFactory, IUserContext userContext) : IQueuedPromptRepository
{
    private const string Columns = "q.id, q.session_id, q.kind, q.text, q.command, q.arguments, q.agent, q.provider_id, q.model_id, q.effort, q.created_at";

    // SQLite's RETURNING can't name a table alias.
    private const string ReturnedColumns = "id, session_id, kind, text, command, arguments, agent, provider_id, model_id, effort, created_at";

    public async Task<IReadOnlyList<QueuedPrompt>> ListAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            $"""
            SELECT {Columns}
            FROM queued_prompts q
            INNER JOIN sessions s ON s.id = q.session_id
            WHERE q.session_id = @SessionId AND s.user_id = @UserId
            ORDER BY q.position, q.created_at
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            Read);
    }

    public Task<bool> AddAsync(QueuedPrompt item)
        => InsertAsync(item, "COALESCE((SELECT MAX(position) FROM queued_prompts WHERE session_id = @SessionId), 0) + 1");

    public Task<bool> ReturnToFrontAsync(QueuedPrompt item)
        => InsertAsync(item, "COALESCE((SELECT MIN(position) FROM queued_prompts WHERE session_id = @SessionId), 1) - 1");

    public async Task<QueuedPrompt?> TakeAsync(string sessionId, string itemId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"""
            DELETE FROM queued_prompts
            WHERE id = @Id AND session_id = @SessionId
              AND EXISTS (SELECT 1 FROM sessions s WHERE s.id = @SessionId AND s.user_id = @UserId)
            RETURNING {ReturnedColumns}
            """,
            cmd =>
            {
                cmd.AddParameter("Id", itemId);
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            Read);
    }

    public async Task<QueuedPrompt?> TakeFirstAsync(string sessionId)
    {
        // One statement, so two callers can't both take the same item.
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            $"""
            DELETE FROM queued_prompts
            WHERE id = (
                SELECT f.id FROM queued_prompts f
                INNER JOIN sessions s ON s.id = f.session_id
                WHERE f.session_id = @SessionId AND s.user_id = @UserId
                ORDER BY f.position, f.created_at
                LIMIT 1)
            RETURNING {ReturnedColumns}
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userContext.UserId);
            },
            Read);
    }

    private async Task<bool> InsertAsync(QueuedPrompt item, string positionSql)
    {
        using var conn = connectionFactory.CreateConnection();
        var inserted = await conn.ExecuteNonQueryAsync(
            $"""
            INSERT INTO queued_prompts (id, session_id, position, kind, text, command, arguments, agent, provider_id, model_id, effort, created_at)
            SELECT @Id, @SessionId, {positionSql}, @Kind, @Text, @Command, @Arguments, @Agent, @ProviderId, @ModelId, @Effort, @CreatedAt
            FROM sessions
            WHERE id = @SessionId AND user_id = @UserId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", item.Id);
                cmd.AddParameter("SessionId", item.SessionId);
                cmd.AddParameter("Kind", item.Kind);
                cmd.AddParameter("Text", item.Text);
                cmd.AddParameter("Command", item.Command);
                cmd.AddParameter("Arguments", item.Arguments);
                cmd.AddParameter("Agent", item.Agent);
                cmd.AddParameter("ProviderId", item.ProviderId);
                cmd.AddParameter("ModelId", item.ModelId);
                cmd.AddParameter("Effort", item.Effort);
                cmd.AddParameter("CreatedAt", item.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                cmd.AddParameter("UserId", userContext.UserId);
            });
        return inserted > 0;
    }

    private static QueuedPrompt Read(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        Kind = r.GetString(r.GetOrdinal("kind")),
        Text = r.GetString(r.GetOrdinal("text")),
        Command = r.GetNullableString(r.GetOrdinal("command")),
        Arguments = r.GetNullableString(r.GetOrdinal("arguments")),
        Agent = r.GetNullableString(r.GetOrdinal("agent")),
        ProviderId = r.GetNullableString(r.GetOrdinal("provider_id")),
        ModelId = r.GetNullableString(r.GetOrdinal("model_id")),
        Effort = r.GetNullableString(r.GetOrdinal("effort")),
        CreatedAt = DateTimeOffset.TryParse(
            r.GetString(r.GetOrdinal("created_at")), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
            ? created
            : DateTimeOffset.UnixEpoch,
    };
}
