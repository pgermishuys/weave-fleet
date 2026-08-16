using System.Text.Json;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Infrastructure.Skills;

/// <summary>
/// File-backed implementation of <see cref="ISkillManifestStore"/>.
/// Stores manifests at ~/.weave/skills/{userId}[_{workspaceId}].json.
/// Writes are atomic (temp file + rename).
/// </summary>
public sealed class JsonSkillManifestStore : ISkillManifestStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        TypeInfoResolver = InfrastructureJsonContext.Default
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private readonly string _skillsDir;

    public JsonSkillManifestStore(string? baseDirectory = null)
    {
        var baseDir = baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _skillsDir = Path.Combine(baseDir, ".weave", "skills");
    }

    public async Task<SkillManifest> LoadAsync(string userId, string? workspaceId = null, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        ValidateWorkspaceId(workspaceId);

        var storePath = GetStorePath(_skillsDir, userId, workspaceId);
        if (!File.Exists(storePath))
        {
            // Return empty manifest if file doesn't exist
            return new SkillManifest
            {
                Id = GenerateManifestId(userId, workspaceId),
                UserId = userId,
                WorkspaceId = workspaceId,
                Skills = [],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await File.ReadAllTextAsync(storePath, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.SkillManifest);
            return manifest ?? throw new InvalidOperationException($"Failed to deserialize manifest at {storePath}");
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(SkillManifest manifest, CancellationToken ct = default)
    {
        ValidateUserId(manifest.UserId);
        ValidateWorkspaceId(manifest.WorkspaceId);

        var storePath = GetStorePath(_skillsDir, manifest.UserId, manifest.WorkspaceId);
        var dir = Path.GetDirectoryName(storePath)!;
        Directory.CreateDirectory(dir);

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Atomic write: write to temp file, then rename
            var tempPath = storePath + ".tmp";
            var json = JsonSerializer.Serialize(manifest, InfrastructureJsonContext.Default.SkillManifest);
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            File.Move(tempPath, storePath, overwrite: true);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task AddEntryAsync(string userId, string? workspaceId, SkillManifestEntry entry, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        ValidateWorkspaceId(workspaceId);

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await LoadInternalAsync(userId, workspaceId, ct).ConfigureAwait(false);
            
            // Check if skill already exists
            if (manifest.Skills.Any(s => s.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Skill '{entry.Name}' already exists in the manifest.");
            }

            var updatedSkills = manifest.Skills.ToList();
            updatedSkills.Add(entry);

            var updatedManifest = manifest with
            {
                Skills = updatedSkills,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveInternalAsync(updatedManifest, _skillsDir, ct).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task RemoveEntryAsync(string userId, string? workspaceId, string skillName, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        ValidateWorkspaceId(workspaceId);

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await LoadInternalAsync(userId, workspaceId, ct).ConfigureAwait(false);
            
            var updatedSkills = manifest.Skills
                .Where(s => !s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If count didn't change, skill wasn't found
            if (updatedSkills.Count == manifest.Skills.Count)
            {
                throw new InvalidOperationException($"Skill '{skillName}' not found in the manifest.");
            }

            var updatedManifest = manifest with
            {
                Skills = updatedSkills,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveInternalAsync(updatedManifest, _skillsDir, ct).ConfigureAwait(false);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task UpdateEntryAsync(string userId, string? workspaceId, SkillManifestEntry entry, CancellationToken ct = default)
    {
        ValidateUserId(userId);
        ValidateWorkspaceId(workspaceId);

        await FileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var manifest = await LoadInternalAsync(userId, workspaceId, ct).ConfigureAwait(false);
            
            var updatedSkills = manifest.Skills.ToList();
            var index = updatedSkills.FindIndex(s => s.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));

            if (index == -1)
            {
                throw new InvalidOperationException($"Skill '{entry.Name}' not found in the manifest.");
            }

            updatedSkills[index] = entry;

            var updatedManifest = manifest with
            {
                Skills = updatedSkills,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveInternalAsync(updatedManifest, _skillsDir, ct).ConfigureAwait(false);
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
    private async Task<SkillManifest> LoadInternalAsync(string userId, string? workspaceId, CancellationToken ct)
    {
        var storePath = GetStorePath(_skillsDir, userId, workspaceId);
        if (!File.Exists(storePath))
        {
            return new SkillManifest
            {
                Id = GenerateManifestId(userId, workspaceId),
                UserId = userId,
                WorkspaceId = workspaceId,
                Skills = [],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        var json = await File.ReadAllTextAsync(storePath, ct).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.SkillManifest);
        return manifest ?? throw new InvalidOperationException($"Failed to deserialize manifest at {storePath}");
    }

    /// <summary>
    /// Internal save that doesn't acquire the lock (caller must hold it).
    /// </summary>
    private static async Task SaveInternalAsync(SkillManifest manifest, string skillsDir, CancellationToken ct)
    {
        var storePath = GetStorePath(skillsDir, manifest.UserId, manifest.WorkspaceId);
        var dir = Path.GetDirectoryName(storePath)!;
        Directory.CreateDirectory(dir);

        var tempPath = storePath + ".tmp";
        var json = JsonSerializer.Serialize(manifest, InfrastructureJsonContext.Default.SkillManifest);
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

    private static string GetStorePath(string skillsDir, string userId, string? workspaceId)
    {
        var fileName = workspaceId is null
            ? $"{userId}.json"
            : $"{userId}_{workspaceId}.json";
        return Path.Combine(skillsDir, fileName);
    }

    private static string GenerateManifestId(string userId, string? workspaceId)
    {
        return workspaceId is null
            ? $"manifest-{userId}"
            : $"manifest-{userId}-{workspaceId}";
    }
}
