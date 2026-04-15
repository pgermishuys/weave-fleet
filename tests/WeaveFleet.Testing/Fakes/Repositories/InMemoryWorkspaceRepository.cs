using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryWorkspaceRepository : IWorkspaceRepository
{
    private readonly Dictionary<string, Workspace> _store = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(Workspace workspace) => _store[workspace.Id] = workspace;

    public void Seed(params Workspace[] workspaces)
    {
        foreach (var w in workspaces)
            Seed(w);
    }

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<Workspace> All => [.. _store.Values];
    public List<Workspace> InsertedWorkspaces { get; } = [];

    // ── IWorkspaceRepository ─────────────────────────────────────────────────

    public Task InsertAsync(Workspace workspace)
    {
        _store[workspace.Id] = workspace;
        InsertedWorkspaces.Add(workspace);
        return Task.CompletedTask;
    }

    public Task<Workspace?> GetByIdAsync(string id)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task<Workspace?> GetByDirectoryAsync(string directory, string isolationStrategy)
        => Task.FromResult(_store.Values.FirstOrDefault(w => w.Directory == directory && w.IsolationStrategy == isolationStrategy));

    public Task<IReadOnlyList<Workspace>> ListAsync()
    {
        IReadOnlyList<Workspace> result = [.. _store.Values];
        return Task.FromResult(result);
    }

    public Task MarkCleanedAsync(string id)
    {
        if (_store.TryGetValue(id, out var workspace))
            workspace.CleanedUpAt = DateTimeOffset.UtcNow.ToString("O");
        return Task.CompletedTask;
    }

    public Task UpdateDisplayNameAsync(string id, string displayName)
    {
        if (_store.TryGetValue(id, out var workspace))
            workspace.DisplayName = displayName;
        return Task.CompletedTask;
    }

    public Task UpdateSourceMetadataAsync(string id, string providerId, string sourceType, string? resourceId, string? resourceUrl, string? title, string? summary, string? resolvedAt)
    {
        if (_store.TryGetValue(id, out var workspace))
        {
            workspace.SourceProviderId = providerId;
            workspace.SourceType = sourceType;
            workspace.SourceResourceId = resourceId;
            workspace.SourceResourceUrl = resourceUrl;
            workspace.SourceTitle = title;
            workspace.SourceSummary = summary;
            workspace.SourceResolvedAt = resolvedAt;
        }
        return Task.CompletedTask;
    }
}
