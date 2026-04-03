using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IWorkspaceRepository
{
    Task InsertAsync(Workspace workspace);
    Task<Workspace?> GetByIdAsync(string id);
    Task<Workspace?> GetByDirectoryAsync(string directory, string isolationStrategy);
    Task<IReadOnlyList<Workspace>> ListAsync();
    Task MarkCleanedAsync(string id);
    Task UpdateDisplayNameAsync(string id, string displayName);
}
