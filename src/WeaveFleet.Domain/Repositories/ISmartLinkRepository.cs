using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISmartLinkRepository
{
    Task<IReadOnlyList<SmartLink>> ListBySessionIdAsync(string sessionId);
    Task<IReadOnlyList<SmartLink>> ListActiveBySessionIdAsync(string sessionId);
    Task<SmartLink?> GetBySessionIdAndUrlAsync(string sessionId, string url);
    Task UpsertAsync(SmartLink smartLink);
    Task DismissAsync(string id);
}
