using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(string id);
    Task<Project?> GetScratchProjectAsync();
    Task<IReadOnlyList<Project>> ListAsync();
    Task InsertAsync(Project project);
    Task UpdateAsync(Project project);
    Task DeleteAsync(string id);
    Task ReorderAsync(string id, int newPosition);
    Task<int> GetSessionCountAsync(string projectId);
    Task MoveSessionsToProjectAsync(string fromProjectId, string toProjectId);
}
