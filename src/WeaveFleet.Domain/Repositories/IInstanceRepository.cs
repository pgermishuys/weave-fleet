using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IInstanceRepository
{
    Task InsertAsync(Instance instance);
    Task<Instance?> GetByIdAsync(string id);
    Task<Instance?> GetByDirectoryAsync(string directory);
    Task<IReadOnlyList<Instance>> ListAsync();
    Task UpdateStatusAsync(string id, string status, string? stoppedAt = null);
    Task<IReadOnlyList<Instance>> GetRunningAsync();
    Task<int> MarkAllStoppedAsync(string stoppedAt);
}
