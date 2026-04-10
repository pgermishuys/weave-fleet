using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISessionSourceUsageRepository
{
    Task InsertAsync(SessionSourceUsage usage);
    Task<IReadOnlyList<SessionSourceUsage>> ListBySessionIdAsync(string sessionId);
}
