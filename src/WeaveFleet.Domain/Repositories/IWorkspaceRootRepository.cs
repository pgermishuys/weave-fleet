using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IWorkspaceRootRepository
{
    Task InsertAsync(WorkspaceRoot root);
    Task<IReadOnlyList<WorkspaceRoot>> ListAsync();
    Task<bool> DeleteAsync(string id);
    Task<WorkspaceRoot?> GetByPathAsync(string path);
}
