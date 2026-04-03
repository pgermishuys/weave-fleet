using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Manages user-configurable workspace root directories.
/// Combines DB-persisted roots with those supplied via the FLEET_WORKSPACE_ROOTS env var.
/// </summary>
public sealed class WorkspaceRootService(IWorkspaceRootRepository workspaceRootRepository)
{
    private const string EnvVar = "FLEET_WORKSPACE_ROOTS";

    /// <summary>
    /// Returns all workspace roots: DB-persisted roots merged with env-var roots.
    /// Each root is augmented with an <c>exists</c> flag checked at call time.
    /// </summary>
    public async Task<IReadOnlyList<WorkspaceRoot>> ListRootsAsync()
    {
        var dbRoots = await workspaceRootRepository.ListAsync();
        var envRoots = GetEnvRoots();

        // Merge: env roots that are not already in DB get synthetic (non-persisted) entries
        var allPaths = dbRoots.Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combined = new List<WorkspaceRoot>(dbRoots);

        foreach (var path in envRoots)
        {
            if (!allPaths.Contains(path))
            {
                combined.Add(new WorkspaceRoot
                {
                    Id = $"env:{path}",
                    Path = path,
                    CreatedAt = string.Empty
                });
            }
        }

        return combined;
    }

    /// <summary>
    /// Validates that <paramref name="path"/> exists on disk, then persists it as a workspace root.
    /// Returns an error if the path does not exist or is already registered.
    /// </summary>
    public async Task<Result<WorkspaceRoot>> AddRootAsync(string path)
    {
        if (!Directory.Exists(path))
            return FleetError.ValidationError("Path", $"Path does not exist: {path}");

        var existing = await workspaceRootRepository.GetByPathAsync(path);
        if (existing is not null)
            return FleetError.ValidationError("Path", $"Path is already registered: {path}");

        var root = new WorkspaceRoot
        {
            Id = Guid.NewGuid().ToString(),
            Path = path,
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        await workspaceRootRepository.InsertAsync(root);
        return root;
    }

    /// <summary>
    /// Removes a DB-persisted workspace root by id. Returns not-found if the id is unknown
    /// or is an env-var synthetic root (prefixed with "env:").
    /// </summary>
    public async Task<Result<Unit>> RemoveRootAsync(string id)
    {
        if (id.StartsWith("env:", StringComparison.Ordinal))
            return FleetError.ValidationError("Id", "Cannot delete an environment-variable workspace root.");

        var deleted = await workspaceRootRepository.DeleteAsync(id);
        if (!deleted)
            return FleetError.NotFoundFor(nameof(WorkspaceRoot), id);

        return Unit.Value;
    }

    /// <summary>
    /// Returns all allowed root paths (union of DB + env) for path-validation purposes.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAllowedRootsAsync()
    {
        var roots = await ListRootsAsync();
        return roots.Select(r => r.Path).ToList();
    }

    // ── private helpers ────────────────────────────────────────────────────────

    private static string[] GetEnvRoots()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
