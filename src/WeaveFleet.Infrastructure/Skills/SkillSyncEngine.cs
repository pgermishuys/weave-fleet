using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Skills;

/// <summary>
/// Copies skills from their source (a GitHub clone under ~/.weave/skills, or a local folder) into
/// harness skill folders, globally or inside a repository. See <see cref="HarnessInstallPaths"/>.
/// </summary>
/// <remarks>
/// Skills are copied on every OS, so a project install can be committed and a global install
/// doesn't depend on Fleet's cache. A folder that's already there is replaced only when Fleet wrote
/// it: it's in the entry's <see cref="SkillManifestEntry.InstalledPaths"/>, it's a link or marked
/// folder from an older Fleet, or it already holds exactly the skill's files.
/// </remarks>
public sealed class SkillSyncEngine : ISkillSyncEngine
{
    private const string LocalOwnerUserId = "local-user";

    private static readonly Action<ILogger, int, Exception?> LogPoolRecycled =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, "PoolRecycled"),
            "Recycled {RecycledCount} idle pooled harness instances after skill sync.");

    private static readonly Action<ILogger, Exception?> LogPoolRecycleFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, "PoolRecycleFailed"),
            "Failed to recycle pooled harness instances after skill sync.");

    private readonly ISkillManifestStore _manifestStore;
    private readonly HarnessInstallPaths _paths;
    private readonly IHarnessPoolRecycler? _poolRecycler;
    private readonly ILogger<SkillSyncEngine> _logger;

    public SkillSyncEngine(
        ISkillManifestStore manifestStore,
        HarnessInstallPaths paths,
        ILogger<SkillSyncEngine> logger,
        IHarnessPoolRecycler? poolRecycler = null)
    {
        _manifestStore = manifestStore;
        _paths = paths;
        _logger = logger;
        _poolRecycler = poolRecycler;
    }

    public async Task<IReadOnlyList<SkillSyncResult>> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await _manifestStore.LoadAsync(LocalOwnerUserId, workspaceId: null, cancellationToken).ConfigureAwait(false);

        var results = new List<SkillSyncResult>();
        var entries = new List<SkillManifestEntry>();

        foreach (var skill in manifest.Skills)
        {
            var skillResults = Sync(skill, cancellationToken);
            results.AddRange(skillResults);
            entries.Add(skill.WithSyncedPaths(skillResults));
        }

        if (results.Any(r => r.Success))
        {
            await _manifestStore.SaveAsync(manifest with { Skills = entries, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken)
                .ConfigureAwait(false);
            await RecyclePooledInstancesAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    public async Task<IReadOnlyList<SkillSyncResult>> SyncSkillAsync(SkillManifestEntry skill, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentException.ThrowIfNullOrWhiteSpace(skill.Name);

        var results = Sync(skill, cancellationToken);

        if (results.Any(r => r.Success))
            await RecyclePooledInstancesAsync(cancellationToken).ConfigureAwait(false);

        return results;
    }

    public async Task<IReadOnlyList<SkillSyncResult>> RemoveSkillAsync(SkillManifestEntry skill, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentException.ThrowIfNullOrWhiteSpace(skill.Name);

        var target = InstallTarget.From(skill.Scope, skill.ProjectPath);
        var results = new List<SkillSyncResult>();

        // Every harness, not just the entry's current ones: the entry may have targeted others before.
        foreach (var harness in HarnessInstallPaths.SkillHarnesses)
        {
            var targetPath = _paths.SkillDirectory(harness, target, skill.Name)!;
            if (!InstalledFiles.Exists(targetPath) || !IsFleetOwned(skill, targetPath))
                continue;

            try
            {
                InstalledFiles.Delete(targetPath);
                results.Add(Succeeded(skill.Name, harness, targetPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(Failed(skill.Name, harness, $"Failed to delete {targetPath}: {ex.Message}", targetPath));
            }
        }

        if (results.Any(r => r.Success))
            await RecyclePooledInstancesAsync(cancellationToken).ConfigureAwait(false);

        return results;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private List<SkillSyncResult> Sync(SkillManifestEntry skill, CancellationToken cancellationToken)
    {
        var sourcePath = ResolveSource(skill);
        if (!Directory.Exists(sourcePath))
        {
            return skill.TargetHarnesses
                .Select(harness => Failed(skill.Name, harness, $"Source directory not found: {sourcePath}"))
                .ToList();
        }

        var target = InstallTarget.From(skill.Scope, skill.ProjectPath);
        return skill.TargetHarnesses
            .Select(harness => SyncToHarness(skill, sourcePath, harness, target, cancellationToken))
            .ToList();
    }

    private SkillSyncResult SyncToHarness(
        SkillManifestEntry skill,
        string sourcePath,
        string harness,
        InstallTarget target,
        CancellationToken cancellationToken)
    {
        var targetPath = _paths.SkillDirectory(harness, target, skill.Name);
        if (targetPath is null)
            return Failed(skill.Name, harness, $"Unknown harness: {harness}");

        // A local skill that already lives in the harness folder: replacing it would delete the source.
        if (InstalledFiles.IsUnder(targetPath, sourcePath) || InstalledFiles.IsUnder(sourcePath, targetPath))
            return Skipped(skill.Name, harness, $"The skill's source is already in the harness folder: {targetPath}", targetPath);

        if (InstalledFiles.Exists(targetPath) && !IsFleetOwned(skill, targetPath) && !InstalledFiles.DirectoriesMatch(sourcePath, targetPath))
        {
            return Skipped(skill.Name, harness,
                $"A skill folder Fleet didn't install is already at {targetPath}. Remove or rename it, then install again.",
                targetPath);
        }

        try
        {
            if (InstalledFiles.Exists(targetPath))
                InstalledFiles.Delete(targetPath);

            InstalledFiles.CopyDirectory(sourcePath, targetPath, cancellationToken);
            return Succeeded(skill.Name, harness, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(skill.Name, harness, $"Failed to sync to {targetPath}: {ex.Message}", targetPath);
        }
    }

    private string ResolveSource(SkillManifestEntry skill) =>
        string.IsNullOrWhiteSpace(skill.LocalPath)
            ? Path.Combine(_paths.SkillCacheDirectory, skill.Name)
            : skill.LocalPath;

    /// <summary>
    /// Fleet wrote this folder: it's recorded on the entry, or it's an older Fleet's install
    /// (a link into the skill cache, or a folder holding the marker file).
    /// </summary>
    private bool IsFleetOwned(SkillManifestEntry skill, string targetPath)
    {
        if (skill.InstalledPaths.Any(p => InstalledFiles.SamePath(p, targetPath)))
            return true;

        if (InstalledFiles.LinkTarget(targetPath) is { } linkTarget)
            return InstalledFiles.IsUnder(linkTarget, _paths.SkillCacheDirectory);

        return File.Exists(Path.Combine(targetPath, InstalledFiles.LegacyManagedMarker));
    }

    private static SkillSyncResult Succeeded(string skillName, string harness, string targetPath) => new()
    {
        SkillName = skillName,
        Harness = harness,
        Success = true,
        Skipped = false,
        TargetPath = targetPath
    };

    private static SkillSyncResult Skipped(string skillName, string harness, string message, string targetPath) => new()
    {
        SkillName = skillName,
        Harness = harness,
        Success = false,
        Skipped = true,
        ErrorMessage = message,
        TargetPath = targetPath
    };

    private static SkillSyncResult Failed(string skillName, string harness, string message, string? targetPath = null) => new()
    {
        SkillName = skillName,
        Harness = harness,
        Success = false,
        Skipped = false,
        ErrorMessage = message,
        TargetPath = targetPath
    };

    /// <summary>
    /// Recycles idle pooled harness instances so they pick up the changed skills.
    /// Best-effort: logs warning on failure but does not throw.
    /// </summary>
    private async Task RecyclePooledInstancesAsync(CancellationToken cancellationToken)
    {
        if (_poolRecycler is null)
            return;

        try
        {
            var recycledCount = await _poolRecycler.RecycleIdleInstancesAsync(cancellationToken).ConfigureAwait(false);
            LogPoolRecycled(_logger, recycledCount, null);
        }
        catch (Exception ex)
        {
            LogPoolRecycleFailed(_logger, ex);
        }
    }
}
