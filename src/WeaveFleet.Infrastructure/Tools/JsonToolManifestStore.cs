using System.Text.Json;
using WeaveFleet.Application.Tools;
using WeaveFleet.Domain.Tools;

namespace WeaveFleet.Infrastructure.Tools;

/// <summary>
/// File-backed implementation of <see cref="IToolManifestStore"/>.
/// Stores manifests at ~/.config/weave-fleet/tool-manifest.json.
/// Writes are atomic (temp file + rename).
/// </summary>
public sealed class JsonToolManifestStore : IToolManifestStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        TypeInfoResolver = InfrastructureJsonContext.Default
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private readonly string _toolsDir;

    public JsonToolManifestStore(string? baseDirectory = null)
    {
        var baseDir = baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _toolsDir = Path.Combine(baseDir, ".config", "weave-fleet");
    }

    public async Task<ToolManifest> LoadAsync(string userId, string? workspaceId = null, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        ValidateWorkspaceId(workspaceId);

        var storePath = GetStorePath(_toolsDir, userId, workspaceId);
        if (!File.Exists(storePath))
        {
            // Return empty manifest if file doesn't exist
            return new ToolManifest
            {
                Id = GenerateManifestId(userId, workspaceId),
                UserId = userId,
                WorkspaceId = workspaceId,
                Tools = [],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await File.ReadAllTextAsync(storePath, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ToolManifest);
            return manifest ?? throw new InvalidOperationException($"Failed to deserialize manifest at {storePath}");
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(ToolManifest manifest, CancellationToken ct = default)
    {
        ValidateUserId(manifest.UserId);
        ValidateWorkspaceId(manifest.WorkspaceId);

        var storePath = GetStorePath(_toolsDir, manifest.UserId, manifest.WorkspaceId);
        var dir = Path.GetDirectoryName(storePath)!;
        Directory.CreateDirectory(dir);

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Atomic write: write to temp file, then rename
            var tempPath = storePath + ".tmp";
            var json = JsonSerializer.Serialize(manifest, InfrastructureJsonContext.Default.ToolManifest);
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            File.Move(tempPath, storePath, overwrite: true);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task AddEntryAsync(string userId, string? workspaceId, ToolManifestEntry entry, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        ValidateWorkspaceId(workspaceId);

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await LoadInternalAsync(userId, workspaceId, ct).ConfigureAwait(false);
            
            // Check if tool already exists
            if (manifest.Tools.Any(t => t.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Tool '{entry.Name}' already exists in the manifest.");
            }

            var updatedTools = manifest.Tools.ToList();
            updatedTools.Add(entry);

            var updatedManifest = manifest with
            {
                Tools = updatedTools,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveInternalAsync(updatedManifest, _toolsDir, ct).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task RemoveEntryAsync(string userId, string? workspaceId, string toolName, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        ValidateWorkspaceId(workspaceId);

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await LoadInternalAsync(userId, workspaceId, ct).ConfigureAwait(false);
            
            var updatedTools = manifest.Tools
                .Where(t => !t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If count didn't change, tool wasn't found
            if (updatedTools.Count == manifest.Tools.Count)
            {
                throw new InvalidOperationException($"Tool '{toolName}' not found in the manifest.");
            }

            var updatedManifest = manifest with
            {
                Tools = updatedTools,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveInternalAsync(updatedManifest, _toolsDir, ct).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task UpdateEntryAsync(string userId, string? workspaceId, ToolManifestEntry entry, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        ValidateWorkspaceId(workspaceId);

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await LoadInternalAsync(userId, workspaceId, ct).ConfigureAwait(false);
            
            var updatedTools = manifest.Tools.ToList();
            var index = updatedTools.FindIndex(t => t.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));

            if (index == -1)
            {
                throw new InvalidOperationException($"Tool '{entry.Name}' not found in the manifest.");
            }

            updatedTools[index] = entry;

            var updatedManifest = manifest with
            {
                Tools = updatedTools,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveInternalAsync(updatedManifest, _toolsDir, ct).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Internal load that doesn't acquire the lock (caller must hold it).
    /// </summary>
    private async Task<ToolManifest> LoadInternalAsync(string userId, string? workspaceId, CancellationToken ct)
    {
        var storePath = GetStorePath(_toolsDir, userId, workspaceId);
        if (!File.Exists(storePath))
        {
            return new ToolManifest
            {
                Id = GenerateManifestId(userId, workspaceId),
                UserId = userId,
                WorkspaceId = workspaceId,
                Tools = [],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        var json = await File.ReadAllTextAsync(storePath, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ToolManifest);
        return manifest ?? throw new InvalidOperationException($"Failed to deserialize manifest at {storePath}");
    }

    /// <summary>
    /// Internal save that doesn't acquire the lock (caller must hold it).
    /// </summary>
    private static async Task SaveInternalAsync(ToolManifest manifest, string toolsDir, CancellationToken ct)
    {
        var storePath = GetStorePath(toolsDir, manifest.UserId, manifest.WorkspaceId);
        var dir = Path.GetDirectoryName(storePath)!;
        Directory.CreateDirectory(dir);

        var tempPath = storePath + ".tmp";
        var json = JsonSerializer.Serialize(manifest, InfrastructureJsonContext.Default.ToolManifest);
        await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
        File.Move(tempPath, storePath, overwrite: true);
    }

    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId cannot be empty.", nameof(userId));

        if (userId.Contains('/', StringComparison.Ordinal) ||
            userId.Contains('\\', StringComparison.Ordinal) ||
            userId.Contains('\0', StringComparison.Ordinal) ||
            userId is "." or "..")
        {
            throw new ArgumentException($"userId contains invalid characters: '{userId}'", nameof(userId));
        }
    }

    private static void ValidateWorkspaceId(string? workspaceId)
    {
        if (workspaceId is null)
            return;

        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("workspaceId cannot be empty when provided.", nameof(workspaceId));

        if (workspaceId.Contains('/', StringComparison.Ordinal) ||
            workspaceId.Contains('\\', StringComparison.Ordinal) ||
            workspaceId.Contains('\0', StringComparison.Ordinal) ||
            workspaceId is "." or "..")
        {
            throw new ArgumentException($"workspaceId contains invalid characters: '{workspaceId}'", nameof(workspaceId));
        }
    }

    private static string GetStorePath(string toolsDir, string userId, string? workspaceId)
    {
        var fileName = workspaceId is null
            ? "tool-manifest.json"
            : $"tool-manifest-{workspaceId}.json";
        return Path.Combine(toolsDir, fileName);
    }

    private static string GenerateManifestId(string userId, string? workspaceId)
    {
        return workspaceId is null
            ? $"tool-manifest-{userId}"
            : $"tool-manifest-{userId}-{workspaceId}";
    }
}
