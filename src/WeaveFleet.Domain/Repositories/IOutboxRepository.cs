using System.Data;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IOutboxRepository
{
    Task<long> EnqueueAsync(OutboxMessage message);
    Task<long> EnqueueAsync(IDbConnection connection, IDbTransaction? transaction, OutboxMessage message);
    Task<IReadOnlyList<OutboxMessage>> GetUndispatchedAsync(int limit);
    Task<IReadOnlyList<OutboxMessage>> GetByTopicAfterAsync(string topic, long sequenceNumber, int limit);
    Task MarkDispatchedAsync(IReadOnlyList<long> ids, string dispatchedAt);
    Task<int> DeleteDispatchedBeforeAsync(string dispatchedBefore, int limit);
}
