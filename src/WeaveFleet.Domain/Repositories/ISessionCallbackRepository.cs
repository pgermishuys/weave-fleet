using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface ISessionCallbackRepository
{
    Task InsertAsync(SessionCallback callback);
    Task<IReadOnlyList<SessionCallback>> GetPendingForSessionAsync(string sourceSessionId);
    Task MarkFiredAsync(string id);
    Task<bool> ClaimPendingAsync(string id);
    Task<IReadOnlyList<SessionCallback>> GetAllPendingAsync();
    Task<int> DeleteForSessionAsync(string sessionId);
}
