using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Manages user-configurable workspace roots for local browsing and repository discovery.
/// Combines DB-persisted roots with those supplied via the FLEET_WORKSPACE_ROOTS env var.
/// </summary>
public sealed class WorkspaceRootService(
    IWorkspaceRootRepository workspaceRootRepository,
    IUserContext userContext)
{
    private const string EnvVar = "FLEET_WORKSPACE_ROOTS";

    /// <summary>
    /// Returns all workspace roots used for local source discovery: DB-persisted roots merged with env-var roots.
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
        var normalizedResult = NormalizeExistingDirectory(path);
        if (normalizedResult.IsFailure)
            return normalizedResult.Error;

        var normalizedPath = normalizedResult.Value;

        var existing = await workspaceRootRepository.GetByPathAsync(normalizedPath);
        if (existing is not null)
            return FleetError.ValidationError("Path", $"Path is already registered: {normalizedPath}");

        var root = new WorkspaceRoot
        {
            Id = Guid.NewGuid().ToString(),
            Path = normalizedPath,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UserId = userContext.UserId
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
    /// Returns all allowed local source roots (union of DB + env) for path-validation purposes.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAllowedRootsAsync()
    {
        var roots = await ListRootsAsync();
        return roots
            .Select(root => CanonicalizePath(root.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<Result<string>> ResolvePathWithinAllowedRootsAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return FleetError.ValidationError("Path", "Path is required.");

        var normalizedResult = NormalizeExistingDirectory(path);
        if (normalizedResult.IsFailure)
            return normalizedResult.Error;

        var normalizedPath = normalizedResult.Value;
        var allowedRoots = await GetAllowedRootsAsync();
        if (!IsPathWithinRoots(normalizedPath, allowedRoots))
            return FleetError.ValidationError("Path", "Path is outside allowed workspace roots.");

        return normalizedPath;
    }

    public static bool IsPathWithinRoots(string path, IReadOnlyList<string> roots)
    {
        var normalizedPath = CanonicalizePath(path);

        foreach (var root in roots)
        {
            var normalizedRoot = CanonicalizePath(root);
            if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;

            if (normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // ── private helpers ────────────────────────────────────────────────────────

    private static Result<string> NormalizeExistingDirectory(string path)
    {
        if (!Directory.Exists(path))
            return FleetError.ValidationError("Path", $"Path does not exist: {path}");

        try
        {
            return CanonicalizePath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FleetError.ValidationError("Path", $"Unable to access path: {path}");
        }
    }

    public static string CanonicalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            return fullPath;

        var currentPath = root;
        var relativeSegments = fullPath[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in relativeSegments)
        {
            currentPath = Path.Combine(currentPath, segment);

            if (!Directory.Exists(currentPath))
                continue;

            var directoryInfo = new DirectoryInfo(currentPath);
            var resolvedTarget = directoryInfo.ResolveLinkTarget(true);
            if (resolvedTarget is not null)
                currentPath = Path.GetFullPath(resolvedTarget.FullName);
        }

        return Path.GetFullPath(currentPath);
    }

    private static string[] GetEnvRoots()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
