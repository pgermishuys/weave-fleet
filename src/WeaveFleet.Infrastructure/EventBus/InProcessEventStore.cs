using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Harnesses;

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
        const string sql = """
            INSERT OR IGNORE INTO inproc_events
                (message_id, session_id, project_id, tenant, event_type,
                 payload, user_id, harness_type, sequence)
            VALUES
                (@MessageId, @SessionId, @ProjectId, @Tenant, @EventType,
                 @Payload, @UserId, @HarnessType, @Sequence);
            SELECT last_insert_rowid();
            """;

        using var conn = _db.CreateConnection();
        var id = conn.ExecuteScalar<long>(sql, new
        {
            envelope.MessageId,
            envelope.SessionId,
            envelope.ProjectId,
            envelope.Tenant,
            envelope.EventType,
            Payload  = JsonSerializer.Serialize(envelope.Event),
            envelope.UserId,
            envelope.HarnessType,
            envelope.Sequence,
        });
        return id; // 0 when INSERT OR IGNORE skipped the row
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
        var rows = conn.Query(sql, new { AfterID = afterId });
        var results = new List<(long, InProcessEnvelope)>();
        foreach (var row in rows)
        {
            HarnessEvent evt;
            try { evt = JsonSerializer.Deserialize<HarnessEvent>((string)row.payload)!; }
            catch (Exception ex)
            {
                LogDeserializationFailed(_logger, (long)row.id, ex);
                continue;
            }

            var env = new InProcessEnvelope(
                Event:       evt,
                MessageId:   (string)row.message_id,
                Tenant:      (string)row.tenant,
                ProjectId:   (string)row.project_id,
                SessionId:   (string)row.session_id,
                EventType:   (string)row.event_type,
                UserId:      (string?)row.user_id,
                HarnessType: (string?)row.harness_type,
                Sequence:    (long)row.sequence,
                IsDurable:   true);
            results.Add(((long)row.id, env));
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
        conn.Execute(sql, new { Id = id });
    }

    [LoggerMessage(Level = LogLevel.Warning, EventId = 1,
        Message = "In-process event store: failed to deserialize payload for event id {EventId} — skipping.")]
    private static partial void LogDeserializationFailed(ILogger logger, long eventId, Exception ex);
}
