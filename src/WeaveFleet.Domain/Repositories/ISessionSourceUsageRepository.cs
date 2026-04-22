using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISessionSourceUsageRepository
{
    Task InsertAsync(SessionSourceUsage usage);
    Task<SessionSourceUsage?> GetPrimaryBySessionIdAsync(string sessionId);
    Task<IReadOnlyDictionary<string, SessionSourceUsage>> GetPrimaryBySessionIdsAsync(IReadOnlyCollection<string> sessionIds);
    Task<IReadOnlyList<SessionSourceUsage>> ListBySessionIdAsync(string sessionId);
}
