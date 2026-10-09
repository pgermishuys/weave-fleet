using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class SessionContextRepository(
    IDbConnectionFactory connectionFactory,
    IUserContext userContext) : ISessionContextRepository
{
    public async Task<SessionContext?> GetAsync(string sessionId, CancellationToken ct)
        => await GetForOwnerAsync(sessionId, userContext.UserId, ct).ConfigureAwait(false);

    public async Task<SessionContext?> GetForOwnerAsync(string sessionId, string userId, CancellationToken ct)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.QueryAsync(
            "SELECT * FROM session_context WHERE session_id = @SessionId AND user_id = @UserId",
            cmd =>
            {
                cmd.AddParameter("SessionId", sessionId);
                cmd.AddParameter("UserId", userId);
            },
            Map,
            ct).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    public async Task<bool> UpsertAsync(SessionContext context, CancellationToken ct)
    {
        using var conn = connectionFactory.CreateConnection();
        var affected = await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO session_context (session_id, user_id, used, context_limit, compacts_at, model_id, provider_id,
                last_call_json, last_call_at, compacting, compacted_at, compaction_error, turns_json, updated_at)
            SELECT @SessionId, @UserId, @Used, @ContextLimit, @CompactsAt, @ModelId, @ProviderId,
                @LastCallJson, @LastCallAt, @Compacting, @CompactedAt, @CompactionError, @TurnsJson, @UpdatedAt
            FROM sessions s
            WHERE s.id = @SessionId AND s.user_id = @UserId
            ON CONFLICT (session_id) DO UPDATE SET
                used = excluded.used,
                context_limit = excluded.context_limit,
                compacts_at = excluded.compacts_at,
                model_id = excluded.model_id,
                provider_id = excluded.provider_id,
                last_call_json = excluded.last_call_json,
                last_call_at = excluded.last_call_at,
                compacting = excluded.compacting,
                compacted_at = excluded.compacted_at,
                compaction_error = excluded.compaction_error,
                turns_json = excluded.turns_json,
                updated_at = excluded.updated_at
            WHERE session_context.user_id = excluded.user_id
            """,
            cmd =>
            {
                cmd.AddParameter("SessionId", context.SessionId);
                cmd.AddParameter("UserId", context.UserId);
                cmd.AddParameter("Used", context.Used);
                cmd.AddParameter("ContextLimit", context.Limit);
                cmd.AddParameter("CompactsAt", context.CompactsAt);
                cmd.AddParameter("ModelId", context.ModelId);
                cmd.AddParameter("ProviderId", context.ProviderId);
                cmd.AddParameter("LastCallJson", context.LastCall is null
                    ? null
                    : JsonSerializer.Serialize(context.LastCall, InfrastructureJsonContext.Default.ContextCall));
                cmd.AddParameter("LastCallAt", Format(context.LastCallAt));
                cmd.AddParameter("Compacting", context.Compacting ? 1 : 0);
                cmd.AddParameter("CompactedAt", Format(context.CompactedAt));
                cmd.AddParameter("CompactionError", context.CompactionError);
                cmd.AddParameter("TurnsJson", JsonSerializer.Serialize(context.Turns.ToList(), InfrastructureJsonContext.Default.ListSessionContextTurn));
                cmd.AddParameter("UpdatedAt", Format(context.UpdatedAt));
            },
            ct).ConfigureAwait(false);
        return affected > 0;
    }

    private static SessionContext Map(DbDataReader r) => new()
    {
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        Used = r.GetNullableInt32(r.GetOrdinal("used")),
        Limit = r.GetNullableInt32(r.GetOrdinal("context_limit")),
        CompactsAt = r.GetNullableInt32(r.GetOrdinal("compacts_at")),
        ModelId = r.GetNullableString(r.GetOrdinal("model_id")),
        ProviderId = r.GetNullableString(r.GetOrdinal("provider_id")),
        LastCall = ReadJson(r.GetNullableString(r.GetOrdinal("last_call_json")), static json => JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ContextCall)),
        LastCallAt = Parse(r.GetNullableString(r.GetOrdinal("last_call_at"))),
        Compacting = r.GetInt32(r.GetOrdinal("compacting")) != 0,
        CompactedAt = Parse(r.GetNullableString(r.GetOrdinal("compacted_at"))),
        CompactionError = r.GetNullableString(r.GetOrdinal("compaction_error")),
        Turns = ReadJson(r.GetString(r.GetOrdinal("turns_json")), static json => JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ListSessionContextTurn)) ?? [],
        UpdatedAt = Parse(r.GetString(r.GetOrdinal("updated_at"))) ?? default,
    };

    private static T? ReadJson<T>(string? json, Func<string, T?> read) where T : class
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return read(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Format(DateTimeOffset? value)
        => value?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? value)
        => value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
