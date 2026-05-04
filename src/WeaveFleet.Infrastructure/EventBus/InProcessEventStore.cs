using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.Infrastructure.EventBus;

/// <summary>
/// Append-only SQLite store for durable in-process bus events.
/// Backed by the <c>inproc_events</c> table (created by migration 018).
/// <para>
/// <see cref="AppendAsync"/> is idempotent: duplicate <c>message_id</c> values are silently
/// ignored (INSERT OR IGNORE). Returns 0 when a duplicate is detected so callers can skip
/// downstream channel writes.
/// </para>
/// <para>
/// <see cref="ReadPendingAsync"/> returns all rows whose <c>dispatched_at</c> is NULL in
/// ascending <c>id</c> order, starting after <paramref name="afterId"/>. Used by
/// <see cref="InProcessProjectionHost"/> to replay events on startup and to drain new events
/// between channel wake-ups.
/// </para>
/// </summary>
internal sealed partial class InProcessEventStore
{
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<InProcessEventStore> _logger;

    public InProcessEventStore(IDbConnectionFactory db, ILogger<InProcessEventStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Appends a durable event to the store.
    /// Returns the assigned <c>id</c> (> 0), or 0 if the <c>message_id</c> already exists.
    /// </summary>
    public long Append(InProcessEnvelope envelope)
    {
        // INSERT and SELECT are separate statements because raw ADO.NET's
        // ExecuteScalar only returns the first result set. With a compound
        // "INSERT …; SELECT last_insert_rowid();" statement, ExecuteScalar
        // returns null (from the INSERT) instead of the rowid, causing every
        // event to be treated as a duplicate and silently dropped.
        const string insertSql = """
            INSERT OR IGNORE INTO inproc_events
                (message_id, session_id, project_id, tenant, event_type,
                 payload, user_id, harness_type, sequence)
            VALUES
                (@MessageId, @SessionId, @ProjectId, @Tenant, @EventType,
                 @Payload, @UserId, @HarnessType, @Sequence)
            """;

        using var conn = _db.CreateConnection();
        string serializedPayload = JsonSerializer.Serialize(envelope.Event, HarnessEventJsonContext.Default.HarnessEvent);

        var rowsAffected = conn.ExecuteNonQuery(insertSql, cmd =>
        {
            cmd.AddParameter("MessageId", envelope.MessageId);
            cmd.AddParameter("SessionId", envelope.SessionId);
            cmd.AddParameter("ProjectId", envelope.ProjectId);
            cmd.AddParameter("Tenant", envelope.Tenant);
            cmd.AddParameter("EventType", envelope.EventType);
            cmd.AddParameter("Payload", serializedPayload);
            cmd.AddParameter("UserId", envelope.UserId);
            cmd.AddParameter("HarnessType", envelope.HarnessType);
            cmd.AddParameter("Sequence", envelope.Sequence);
        });

        if (rowsAffected == 0)
            return 0; // INSERT OR IGNORE skipped the row (duplicate message_id)

        var id = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", _ => { });
        return id;
    }

    /// <summary>
    /// Returns all pending (undispatched) events with <c>id &gt; afterId</c>, in ascending order.
    /// </summary>
    public IReadOnlyList<(long Id, InProcessEnvelope Envelope)> ReadPending(long afterId)
    {
        const string sql = """
            SELECT id, message_id, session_id, project_id, tenant, event_type,
                   payload, user_id, harness_type, sequence
            FROM   inproc_events
            WHERE  dispatched_at IS NULL AND id > @AfterID
            ORDER  BY id
            LIMIT  500
            """;

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var p = cmd.CreateParameter();
        p.ParameterName = "@AfterID";
        p.Value = afterId;
        cmd.Parameters.Add(p);

        if (conn.State != ConnectionState.Open)
            conn.Open();

        using var reader = cmd.ExecuteReader();
        var results = new List<(long, InProcessEnvelope)>();
        while (reader.Read())
        {
            var id = reader.GetInt64(reader.GetOrdinal("id"));
            var payloadJson = reader.GetString(reader.GetOrdinal("payload"));

            HarnessEvent evt;
            try { evt = JsonSerializer.Deserialize(payloadJson, HarnessEventJsonContext.Default.HarnessEvent)!; }
            catch (Exception ex)
            {
                LogDeserializationFailed(_logger, id, ex);
                continue;
            }

            var userIdOrdinal = reader.GetOrdinal("user_id");
            var harnessTypeOrdinal = reader.GetOrdinal("harness_type");

            var env = new InProcessEnvelope(
                Event:       evt,
                MessageId:   reader.GetString(reader.GetOrdinal("message_id")),
                Tenant:      reader.GetString(reader.GetOrdinal("tenant")),
                ProjectId:   reader.GetString(reader.GetOrdinal("project_id")),
                SessionId:   reader.GetString(reader.GetOrdinal("session_id")),
                EventType:   reader.GetString(reader.GetOrdinal("event_type")),
                UserId:      reader.IsDBNull(userIdOrdinal) ? null : reader.GetString(userIdOrdinal),
                HarnessType: reader.IsDBNull(harnessTypeOrdinal) ? null : reader.GetString(harnessTypeOrdinal),
                Sequence:    reader.GetInt64(reader.GetOrdinal("sequence")),
                IsDurable:   true);
            results.Add((id, env));
        }
        return results;
    }

    /// <summary>Marks a single event as dispatched so it is not replayed on next startup.</summary>
    public void MarkDispatched(long id)
    {
        const string sql = """
            UPDATE inproc_events
            SET    dispatched_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE  id = @Id
            """;
        using var conn = _db.CreateConnection();
        conn.ExecuteNonQuery(sql, cmd => { cmd.AddParameter("Id", id); });
    }

    [LoggerMessage(Level = LogLevel.Warning, EventId = 1,
        Message = "In-process event store: failed to deserialize payload for event id {EventId} — skipping.")]
    private static partial void LogDeserializationFailed(ILogger logger, long eventId, Exception ex);
}
