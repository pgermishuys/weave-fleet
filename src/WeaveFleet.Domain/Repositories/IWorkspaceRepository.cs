using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IWorkspaceRepository
{
    Task InsertAsync(Workspace workspace);
    Task<Workspace?> GetByIdAsync(string id);
    Task<Workspace?> GetByDirectoryAsync(string directory, string isolationStrategy);
    Task<IReadOnlyList<Workspace>> ListAsync();
    Task MarkCleanedAsync(string id);
    /// <summary>The checkout is back: the workspace's folder is in use again.</summary>
    Task ClearCleanedAsync(string id);
    /// <summary>The branch the checkout is on now, which is the one it comes back on.</summary>
    Task UpdateBranchAsync(string id, string branch);
    Task UpdateDisplayNameAsync(string id, string displayName);
    Task UpdateSourceMetadataAsync(string id, string providerId, string sourceType, string? resourceId, string? resourceUrl, string? title, string? summary, string? resolvedAt);
}
