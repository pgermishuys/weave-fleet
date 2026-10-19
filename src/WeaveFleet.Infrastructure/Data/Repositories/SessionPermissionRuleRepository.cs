using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

/// <summary>
/// The <c>session_permission_rules</c> table (migration 065). Not scoped to a user: harness sessions read it on their
/// own, outside any request; the API checks the session is the user's before it calls.
/// </summary>
public sealed class SessionPermissionRuleRepository(IDbConnectionFactory connectionFactory) : ISessionPermissionRuleRepository
{
    private const string Columns = "id, session_id, kind, tool, patterns, created_at";

    public async Task<IReadOnlyList<SessionPermissionRule>> ListAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryAsync(
            $"SELECT {Columns} FROM session_permission_rules WHERE session_id = @SessionId ORDER BY created_at, rowid",
            cmd => cmd.AddParameter("SessionId", sessionId),
            Read);
    }

    public async Task<bool> AddAsync(SessionPermissionRule rule)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteNonQueryAsync(
            """
            INSERT INTO session_permission_rules (id, session_id, kind, tool, patterns, created_at)
            SELECT @Id, @SessionId, @Kind, @Tool, @Patterns, @CreatedAt FROM sessions WHERE id = @SessionId
            """,
            cmd =>
            {
                cmd.AddParameter("Id", rule.Id);
                cmd.AddParameter("SessionId", rule.SessionId);
                cmd.AddParameter("Kind", rule.Kind);
                cmd.AddParameter("Tool", rule.Tool);
                cmd.AddParameter("Patterns", WritePatterns(rule.Patterns));
                cmd.AddParameter("CreatedAt", rule.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            });
        return rows > 0;
    }

    public async Task<bool> DeleteAsync(string sessionId, string ruleId)
    {
        using var conn = connectionFactory.CreateConnection();
        var rows = await conn.ExecuteNonQueryAsync(
            "DELETE FROM session_permission_rules WHERE id = @Id AND session_id = @SessionId",
            cmd =>
            {
                cmd.AddParameter("Id", ruleId);
                cmd.AddParameter("SessionId", sessionId);
            });
        return rows > 0;
    }

    public async Task<int> DeleteAllAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.ExecuteNonQueryAsync(
            "DELETE FROM session_permission_rules WHERE session_id = @SessionId",
            cmd => cmd.AddParameter("SessionId", sessionId));
    }

    public async Task CopyAsync(string fromSessionId, string toSessionId)
    {
        foreach (var rule in await ListAsync(fromSessionId).ConfigureAwait(false))
        {
            await AddAsync(rule with { Id = $"rule_{Guid.NewGuid():N}", SessionId = toSessionId }).ConfigureAwait(false);
        }
    }

    public async Task<string?> OwnerAsync(string sessionId)
    {
        using var conn = connectionFactory.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync(
            "SELECT user_id FROM sessions WHERE id = @SessionId",
            cmd => cmd.AddParameter("SessionId", sessionId),
            r => r.GetString(0));
    }

    private static string WritePatterns(IReadOnlyList<string> patterns)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var pattern in patterns)
                writer.WriteStringValue(pattern);
            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static List<string> ReadPatterns(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? [.. document.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static SessionPermissionRule Read(DbDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        SessionId = r.GetString(r.GetOrdinal("session_id")),
        Kind = r.GetString(r.GetOrdinal("kind")),
        Tool = r.GetString(r.GetOrdinal("tool")),
        Patterns = ReadPatterns(r.GetString(r.GetOrdinal("patterns"))),
        CreatedAt = DateTimeOffset.TryParse(r.GetString(r.GetOrdinal("created_at")), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : DateTimeOffset.UnixEpoch,
    };
}
