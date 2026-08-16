using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Skills;

/// <summary>
/// Ensures bundled skills are registered in the manifest and synced to harness paths on startup.
/// </summary>
/// <remarks>
/// <para>
/// Bundled skills are read from a known directory in the distribution (currently a placeholder).
/// On startup, this service:
/// <list type="number">
/// <item>Scans the bundled skills directory for available skills.</item>
/// <item>Loads the user's skill manifest.</item>
/// <item>Adds any missing bundled skills to the manifest with <c>Source=Bundled</c>.</item>
/// <item>Re-adds any bundled skills that were removed from the manifest (bundled skills are always present).</item>
/// <item>Triggers a full sync to deploy all skills to their target harnesses.</item>
/// </list>
/// </para>
/// <para>
/// <strong>Local mode:</strong> Uses the deterministic <c>"local-user"</c> sentinel (matching
/// <see cref="LocalUserContext"/>). Bundled skills are deployed to the local user's harness paths.
/// </para>
/// <para>
/// <strong>Auth-enabled mode:</strong> Skips bundled skill deployment. In multi-tenant hosted mode,
/// bundled skills would need to be deployed per-user, which requires authenticated context.
/// For now, bundled skills are a local-mode feature only.
/// </para>
/// </remarks>
public sealed partial class BundledSkillsHostedService : IHostedService
{
    private const string LocalOwnerUserId = "local-user";
    
    // Placeholder: bundled skills will be embedded or distributed in a known directory.
    // For now, we use an empty directory to establish the startup logic.
    private static readonly string BundledSkillsDir =
        Path.Combine(AppContext.BaseDirectory, "bundled-skills");

    private readonly ISkillManifestStore _manifestStore;
    private readonly ISkillSyncEngine _syncEngine;
    private readonly FleetOptions _options;
    private readonly ILogger<BundledSkillsHostedService> _logger;

    public BundledSkillsHostedService(
        ISkillManifestStore manifestStore,
        ISkillSyncEngine syncEngine,
        FleetOptions options,
        ILogger<BundledSkillsHostedService> logger)
    {
        _manifestStore = manifestStore;
        _syncEngine = syncEngine;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Auth-enabled: bundled skills are a local-mode feature only.
        // In multi-tenant mode, skills would need per-user deployment with authenticated context.
        if (_options.Auth.Enabled)
        {
            LogSkippedAuthEnabled(_logger);
            return;
        }

        try
        {
            // Discover bundled skills
            var bundledSkills = DiscoverBundledSkills();
            if (bundledSkills.Count == 0)
            {
                LogNoBundledSkills(_logger, BundledSkillsDir);
                return;
            }

            LogDiscoveredBundledSkills(_logger, bundledSkills.Count);

            // Load the user's manifest
            var manifest = await _manifestStore.LoadAsync(LocalOwnerUserId, workspaceId: null, cancellationToken)
                .ConfigureAwait(false);

            // Track whether we made any changes
            var manifestChanged = false;

            // Add or re-add bundled skills
            var updatedSkills = manifest.Skills.ToList();
            var now = DateTimeOffset.UtcNow;

            foreach (var bundledSkill in bundledSkills)
            {
                var existing = updatedSkills.FirstOrDefault(s =>
                    s.Name.Equals(bundledSkill.Name, StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    // Skill not in manifest - add it
                    updatedSkills.Add(new SkillManifestEntry
                    {
                        Name = bundledSkill.Name,
                        Source = SkillSource.Bundled,
                        TargetHarnesses = bundledSkill.TargetHarnesses,
                        InstalledAt = now,
                        UpdatedAt = now
                    });
                    manifestChanged = true;
                    LogBundledSkillAdded(_logger, bundledSkill.Name);
                }
                else if (existing.Source != SkillSource.Bundled)
                {
                    // Skill exists but source is wrong - update it to Bundled
                    var index = updatedSkills.IndexOf(existing);
                    updatedSkills[index] = existing with
                    {
                        Source = SkillSource.Bundled,
                        TargetHarnesses = bundledSkill.TargetHarnesses,
                        UpdatedAt = now
                    };
                    manifestChanged = true;
                    LogBundledSkillSourceCorrected(_logger, bundledSkill.Name, existing.Source);
                }
                else
                {
                    // Skill exists and is already marked as Bundled - ensure target harnesses are up to date
                    if (!existing.TargetHarnesses.SequenceEqual(bundledSkill.TargetHarnesses))
                    {
                        var index = updatedSkills.IndexOf(existing);
                        updatedSkills[index] = existing with
                        {
                            TargetHarnesses = bundledSkill.TargetHarnesses,
                            UpdatedAt = now
                        };
                        manifestChanged = true;
                        LogBundledSkillHarnessesUpdated(_logger, bundledSkill.Name);
                    }
                }
            }

            // Save manifest if changed
            if (manifestChanged)
            {
                var updatedManifest = manifest with
                {
                    Skills = updatedSkills,
                    UpdatedAt = now
                };
                await _manifestStore.SaveAsync(updatedManifest, cancellationToken).ConfigureAwait(false);
                LogManifestUpdated(_logger);
            }

            // Sync all skills to harness paths
            using (BackgroundUserContext.BeginScope(LocalOwnerUserId))
            {
                var syncResults = await _syncEngine.SyncAllAsync(cancellationToken).ConfigureAwait(false);
                
                var successCount = syncResults.Count(r => r.Success);
                var failureCount = syncResults.Count(r => !r.Success && !r.Skipped);
                var skippedCount = syncResults.Count(r => r.Skipped);

                LogSyncCompleted(_logger, successCount, failureCount, skippedCount);

                // Log individual failures
                foreach (var failure in syncResults.Where(r => !r.Success && !r.Skipped))
                {
                    LogSyncFailed(_logger, failure.SkillName, failure.Harness, failure.ErrorMessage ?? "Unknown error");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogCancelled(_logger);
        }
        catch (Exception ex)
        {
            // Best-effort: log but don't block application startup
            LogFailed(_logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Discovers bundled skills from the bundled skills directory.
    /// Each subdirectory is treated as a skill if it contains a SKILL.md file.
    /// </summary>
    private static List<BundledSkillDescriptor> DiscoverBundledSkills()
    {
        if (!Directory.Exists(BundledSkillsDir))
        {
            return [];
        }

        var skills = new List<BundledSkillDescriptor>();

        foreach (var skillDir in Directory.GetDirectories(BundledSkillsDir))
        {
            var skillName = Path.GetFileName(skillDir);
            var skillFile = Path.Combine(skillDir, "SKILL.md");

            // A valid bundled skill must have a SKILL.md file
            if (!File.Exists(skillFile))
            {
                continue;
            }

            // For now, all bundled skills target both opencode and claude-code
            // In the future, this could be read from metadata in SKILL.md
            skills.Add(new BundledSkillDescriptor
            {
                Name = skillName,
                TargetHarnesses = ["opencode", "claude-code"]
            });
        }

        return skills;
    }

    // ── Logging ────────────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Bundled skills deployment skipped: auth-enabled mode. Bundled skills are a local-mode feature only.")]
    private static partial void LogSkippedAuthEnabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "No bundled skills found in {BundledSkillsDir}.")]
    private static partial void LogNoBundledSkills(ILogger logger, string bundledSkillsDir);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Discovered {Count} bundled skill(s).")]
    private static partial void LogDiscoveredBundledSkills(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Added bundled skill '{SkillName}' to manifest.")]
    private static partial void LogBundledSkillAdded(ILogger logger, string skillName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Corrected source for skill '{SkillName}' from {OldSource} to Bundled.")]
    private static partial void LogBundledSkillSourceCorrected(ILogger logger, string skillName, SkillSource oldSource);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Updated target harnesses for bundled skill '{SkillName}'.")]
    private static partial void LogBundledSkillHarnessesUpdated(ILogger logger, string skillName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Skill manifest updated with bundled skills.")]
    private static partial void LogManifestUpdated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Skill sync completed: {SuccessCount} succeeded, {FailureCount} failed, {SkippedCount} skipped.")]
    private static partial void LogSyncCompleted(ILogger logger, int successCount, int failureCount, int skippedCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to sync skill '{SkillName}' to harness '{Harness}': {ErrorMessage}")]
    private static partial void LogSyncFailed(ILogger logger, string skillName, string harness, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Bundled skills deployment cancelled: application shutting down.")]
    private static partial void LogCancelled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Bundled skills deployment failed (best-effort); application boot continues normally.")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    // ── Nested types ───────────────────────────────────────────────────────────

    private sealed record BundledSkillDescriptor
    {
        public required string Name { get; init; }
        public required IReadOnlyList<string> TargetHarnesses { get; init; }
    }
}
