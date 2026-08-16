using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Infrastructure.Skills;

/// <summary>
/// Synchronizes skills from ~/.weave/skills/{name}/ to harness discovery paths.
/// Creates symlinks on macOS/Linux, copies on Windows.
/// Tracks Fleet-managed skills via a .fleet-managed marker file.
/// </summary>
public sealed class SkillSyncEngine : ISkillSyncEngine
{
    private const string FleetManagedMarker = ".fleet-managed";

    private readonly string _weaveSkillsDir;
    private readonly Dictionary<string, string> _harnessDiscoveryPaths;

    private static readonly Action<ILogger, int, Exception?> LogPoolRecycled =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, "PoolRecycled"),
            "Recycled {RecycledCount} idle pooled harness instances after skill sync.");

    private static readonly Action<ILogger, Exception?> LogPoolRecycleFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, "PoolRecycleFailed"),
            "Failed to recycle pooled harness instances after skill sync.");

    private readonly ISkillManifestStore _manifestStore;
    private readonly IHarnessPoolRecycler? _poolRecycler;
    private readonly ILogger<SkillSyncEngine> _logger;

    public SkillSyncEngine(
        ISkillManifestStore manifestStore,
        ILogger<SkillSyncEngine> logger,
        IHarnessPoolRecycler? poolRecycler = null,
        string? baseDirectory = null)
    {
        _manifestStore = manifestStore;
        _logger = logger;
        _poolRecycler = poolRecycler;

        var baseDir = baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _weaveSkillsDir = Path.Combine(baseDir, ".weave", "skills");
        _harnessDiscoveryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["opencode"] = Path.Combine(baseDir, ".config", "opencode", "skills"),
            ["claude-code"] = Path.Combine(baseDir, ".claude", "skills")
        };
    }

    public async Task<IReadOnlyList<SkillSyncResult>> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        // Load manifest for the current user (using "local-user" as default)
        var manifest = await _manifestStore.LoadAsync("local-user", workspaceId: null, cancellationToken).ConfigureAwait(false);

        var results = new List<SkillSyncResult>();

        foreach (var skill in manifest.Skills)
        {
            var skillResults = await SyncSkillInternalAsync(skill, cancellationToken).ConfigureAwait(false);
            results.AddRange(skillResults);
        }

        // Recycle idle pooled instances if any skills were successfully synced
        if (results.Any(r => r.Success) && _poolRecycler is not null)
        {
            await RecyclePooledInstancesAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    public async Task<IReadOnlyList<SkillSyncResult>> SyncSkillAsync(string skillName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            throw new ArgumentException("Skill name cannot be empty.", nameof(skillName));

        // Load manifest for the current user
        var manifest = await _manifestStore.LoadAsync("local-user", workspaceId: null, cancellationToken).ConfigureAwait(false);

        var skill = manifest.Skills.FirstOrDefault(s => s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            return
            [
                new SkillSyncResult
                {
                    SkillName = skillName,
                    Harness = "unknown",
                    Success = false,
                    Skipped = false,
                    ErrorMessage = $"Skill '{skillName}' not found in manifest."
                }
            ];
        }

        var results = await SyncSkillInternalAsync(skill, cancellationToken).ConfigureAwait(false);

        // Recycle idle pooled instances if the skill was successfully synced
        if (results.Any(r => r.Success) && _poolRecycler is not null)
        {
            await RecyclePooledInstancesAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<SkillSyncResult>> SyncSkillInternalAsync(
        SkillManifestEntry skill,
        CancellationToken cancellationToken)
    {
        var results = new List<SkillSyncResult>();

        // Resolve source path
        var sourcePath = Path.Combine(_weaveSkillsDir, skill.Name);
        if (!Directory.Exists(sourcePath))
        {
            // Source doesn't exist - return error for all harnesses
            foreach (var harness in skill.TargetHarnesses)
            {
                results.Add(new SkillSyncResult
                {
                    SkillName = skill.Name,
                    Harness = harness,
                    Success = false,
                    Skipped = false,
                    ErrorMessage = $"Source directory not found: {sourcePath}"
                });
            }
            return results;
        }

        // Sync to each target harness
        foreach (var harness in skill.TargetHarnesses)
        {
            var result = await SyncToHarnessAsync(skill.Name, sourcePath, harness, cancellationToken).ConfigureAwait(false);
            results.Add(result);
        }

        return results;
    }

    private async Task<SkillSyncResult> SyncToHarnessAsync(
        string skillName,
        string sourcePath,
        string harness,
        CancellationToken cancellationToken)
    {
        // Resolve harness discovery path
        if (!_harnessDiscoveryPaths.TryGetValue(harness, out var harnessBasePath))
        {
            return new SkillSyncResult
            {
                SkillName = skillName,
                Harness = harness,
                Success = false,
                Skipped = false,
                ErrorMessage = $"Unknown harness: {harness}"
            };
        }

        var targetPath = Path.Combine(harnessBasePath, skillName);
        var markerPath = Path.Combine(targetPath, FleetManagedMarker);

        // Check if target exists and is user-managed
        if (Directory.Exists(targetPath) && !File.Exists(markerPath))
        {
            return new SkillSyncResult
            {
                SkillName = skillName,
                Harness = harness,
                Success = false,
                Skipped = true,
                ErrorMessage = $"Target path exists but is not Fleet-managed (missing {FleetManagedMarker}): {targetPath}",
                TargetPath = targetPath
            };
        }

        try
        {
            // Remove existing Fleet-managed target if present
            if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, recursive: true);
            }

            // Ensure parent directory exists
            Directory.CreateDirectory(harnessBasePath);

            // Create symlink on macOS/Linux, copy on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await CopyDirectoryAsync(sourcePath, targetPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Directory.CreateSymbolicLink(targetPath, sourcePath);
            }

            // Write Fleet-managed marker
            await File.WriteAllTextAsync(markerPath, $"Managed by Weave Fleet\nSource: {sourcePath}\n", cancellationToken).ConfigureAwait(false);

            return new SkillSyncResult
            {
                SkillName = skillName,
                Harness = harness,
                Success = true,
                Skipped = false,
                TargetPath = targetPath
            };
        }
        catch (Exception ex)
        {
            return new SkillSyncResult
            {
                SkillName = skillName,
                Harness = harness,
                Success = false,
                Skipped = false,
                ErrorMessage = $"Failed to sync to {targetPath}: {ex.Message}",
                TargetPath = targetPath
            };
        }
    }

    /// <summary>
    /// Recursively copies a directory and its contents.
    /// </summary>
    private static async Task CopyDirectoryAsync(string sourceDir, string targetDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDir);

        // Copy files
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(targetDir, fileName);
            File.Copy(file, targetFile, overwrite: true);
        }

        // Copy subdirectories
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dirName = Path.GetFileName(subDir);
            var targetSubDir = Path.Combine(targetDir, dirName);
            await CopyDirectoryAsync(subDir, targetSubDir, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Recycles idle pooled harness instances after skill sync.
    /// Best-effort: logs warning on failure but does not throw.
    /// </summary>
    private async Task RecyclePooledInstancesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var recycledCount = await _poolRecycler!.RecycleIdleInstancesAsync(cancellationToken).ConfigureAwait(false);
            LogPoolRecycled(_logger, recycledCount, null);
        }
        catch (Exception ex)
        {
            LogPoolRecycleFailed(_logger, ex);
        }
    }
}
