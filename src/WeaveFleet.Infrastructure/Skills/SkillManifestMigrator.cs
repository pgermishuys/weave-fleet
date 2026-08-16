using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Infrastructure.Skills;

/// <summary>
/// Migrates existing skills from ~/.weave/skills/ to the new manifest-based system.
/// Runs once on first startup when skills.json doesn't exist but skill folders do.
/// </summary>
public sealed class SkillManifestMigrator
{
    private static readonly string SkillsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "skills");

    private static readonly string[] RegisteredHarnesses = ["opencode", "claude-code"];

    private readonly ISkillManifestStore _manifestStore;

    public SkillManifestMigrator(ISkillManifestStore manifestStore)
    {
        _manifestStore = manifestStore;
    }

    /// <summary>
    /// Migrates existing skills from ~/.weave/skills/ to the manifest.
    /// Idempotent: skips migration if manifest already exists.
    /// </summary>
    /// <param name="userId">The user ID to migrate skills for.</param>
    /// <param name="workspaceId">Optional workspace ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of skills migrated (0 if manifest already exists or no skills found).</returns>
    public async Task<int> MigrateAsync(string userId, string? workspaceId = null, CancellationToken ct = default)
    {
        // Load manifest — if it already has skills, skip migration
        var manifest = await _manifestStore.LoadAsync(userId, workspaceId, ct).ConfigureAwait(false);
        if (manifest.Skills.Count > 0)
        {
            // Manifest already exists with skills, skip migration
            return 0;
        }

        // Check if ~/.weave/skills/ directory exists
        if (!Directory.Exists(SkillsDir))
        {
            return 0;
        }

        // Discover existing skill folders
        var skillFolders = Directory.GetDirectories(SkillsDir)
            .Where(dir => !Path.GetFileName(dir).StartsWith('.') && !Path.GetFileName(dir).EndsWith(".json", StringComparison.Ordinal))
            .ToList();

        if (skillFolders.Count == 0)
        {
            return 0;
        }

        // Create manifest entries for each skill
        var now = DateTimeOffset.UtcNow;
        var entries = new List<SkillManifestEntry>();

        foreach (var skillFolder in skillFolders)
        {
            var skillName = Path.GetFileName(skillFolder);
            
            // Skip if this looks like a manifest file or temp directory
            if (string.IsNullOrWhiteSpace(skillName))
            {
                continue;
            }

            var entry = new SkillManifestEntry
            {
                Name = skillName,
                Source = SkillSource.Local,
                LocalPath = skillFolder,
                TargetHarnesses = RegisteredHarnesses,
                InstalledAt = now,
                UpdatedAt = now
            };

            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            return 0;
        }

        // Save manifest with all migrated entries
        var updatedManifest = manifest with
        {
            Skills = entries,
            UpdatedAt = now
        };

        await _manifestStore.SaveAsync(updatedManifest, ct).ConfigureAwait(false);

        return entries.Count;
    }
}
