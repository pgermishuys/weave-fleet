using System.Data;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Append-only per-session log of durable harness events. Populated by the
/// <c>MessagePersistenceProjection</c> in the same transaction as the message upsert,
/// keyed by the relay's per-pump monotonic sequence number for idempotency on
/// JetStream redelivery.
/// </summary>
public interface IHarnessEventLogRepository
{
    /// <summary>
    /// Idempotently append a row using the supplied connection / transaction. If a row
    /// with the same (session_id, sequence_number) already exists, this is a no-op and
    /// returns the existing row id.
    /// </summary>
    Task<long> AppendAsync(IDbConnection connection, IDbTransaction? transaction, HarnessEventLogEntry entry);

    /// <summary>
    /// Idempotently append a row using a fresh connection. Use this from background
    /// projections that do not already have a transaction open.
    /// </summary>
    Task<long> AppendAsync(HarnessEventLogEntry entry);

    /// <summary>
    /// Return entries for the session with sequence_number &gt; <paramref name="afterSequenceNumber"/>,
    /// ordered ascending, capped at <paramref name="limit"/>. Filters by
    /// <c>user_id</c> to enforce per-user scoping.
    /// </summary>
    Task<IReadOnlyList<HarnessEventLogEntry>> GetBySessionAfterAsync(
        string sessionId,
        long afterSequenceNumber,
        int limit);
}
