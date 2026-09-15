using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Skills;
using WeaveFleet.Application.Tools;
using WeaveFleet.Domain.Skills;
using WeaveFleet.Domain.Tools;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tools;

/// <summary>
/// Moves skills and tools installed by older Fleet versions to where OpenCode reads them. Runs at
/// startup for entries with no recorded install path; once moved, an entry records one and is skipped.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Skills were symlinked into the global skills folder: they're replaced with copies.</item>
/// <item>Native tools were copied to ~/.config/weave-fleet/tools, which OpenCode never read: they're
/// installed into OpenCode's global tools folder and the old copy is deleted.</item>
/// <item>MCP servers were written under <c>mcpServers</c>, a key OpenCode doesn't know: they move to <c>mcp</c>.</item>
/// <item>The catalog's <c>fleet-api</c> skill now comes with Fleet, and its <c>visualize</c> tool gave way to the
/// canvas tools: Fleet's global installs of both are removed, so an old copy can't shadow the one Fleet ships.</item>
/// </list>
/// Anything that would overwrite a file Fleet didn't write is left alone and logged; the Settings
/// page shows those entries as not installed. Local mode only, like bundled skills.
/// </remarks>
public sealed partial class LegacyInstallMigrationHostedService : IHostedService
{
    private const string LocalOwnerUserId = "local-user";

    /// <summary>Where the catalog's skills and tools came from.</summary>
    private const string FleetRepoUrl = "https://github.com/pgermishuys/weave-fleet";

    private static readonly string[] RetiredSkills = ["fleet-api"];
    private static readonly string[] RetiredTools = ["visualize"];

    private readonly ISkillManifestStore _skillStore;
    private readonly ISkillSyncEngine _syncEngine;
    private readonly IToolManifestStore _toolStore;
    private readonly IToolInstaller _installer;
    private readonly HarnessInstallPaths _paths;
    private readonly FleetOptions _options;
    private readonly ILogger<LegacyInstallMigrationHostedService> _logger;

    public LegacyInstallMigrationHostedService(
        ISkillManifestStore skillStore,
        ISkillSyncEngine syncEngine,
        IToolManifestStore toolStore,
        IToolInstaller installer,
        HarnessInstallPaths paths,
        FleetOptions options,
        ILogger<LegacyInstallMigrationHostedService> logger)
    {
        _skillStore = skillStore;
        _syncEngine = syncEngine;
        _toolStore = toolStore;
        _installer = installer;
        _paths = paths;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.Auth.Enabled)
            return;

        try
        {
            await MigrateSkillsAsync(cancellationToken).ConfigureAwait(false);
            await MigrateToolsAsync(cancellationToken).ConfigureAwait(false);
            await RemoveRetiredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Best-effort: never block startup.
            LogFailed(ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task MigrateSkillsAsync(CancellationToken cancellationToken)
    {
        var manifest = await _skillStore.LoadAsync(LocalOwnerUserId, workspaceId: null, cancellationToken).ConfigureAwait(false);

        foreach (var skill in manifest.Skills.Where(s => s.InstalledPaths.Count == 0 && s.Scope == InstallScope.Global))
        {
            var results = await _syncEngine.SyncSkillAsync(skill, cancellationToken).ConfigureAwait(false);
            foreach (var problem in results.Where(r => !r.Success))
                LogSkillNotMigrated(skill.Name, problem.ErrorMessage ?? "Unknown error");

            if (results.Any(r => r.Success))
            {
                await _skillStore.UpdateEntryAsync(manifest.UserId, manifest.WorkspaceId, skill.WithSyncedPaths(results), cancellationToken)
                    .ConfigureAwait(false);
                LogSkillMigrated(skill.Name);
            }
        }
    }

    internal async Task MigrateToolsAsync(CancellationToken cancellationToken)
    {
        var manifest = await _toolStore.LoadAsync(LocalOwnerUserId, workspaceId: null, cancellationToken).ConfigureAwait(false);
        var legacyToolsDir = Path.Combine(_paths.HomeDirectory, ".config", "weave-fleet", "tools", manifest.UserId);

        foreach (var tool in manifest.Tools.Where(t => t.InstalledPath is null && t.Scope == InstallScope.Global))
        {
            var installedPath = tool.ToolType == ToolType.Mcp
                ? await MigrateMcpAsync(tool, cancellationToken).ConfigureAwait(false)
                : await MigrateNativeAsync(tool, legacyToolsDir, cancellationToken).ConfigureAwait(false);

            if (installedPath is null)
                continue;

            await _toolStore.UpdateEntryAsync(manifest.UserId, manifest.WorkspaceId, tool with { InstalledPath = installedPath }, cancellationToken)
                .ConfigureAwait(false);
            LogToolMigrated(tool.Name, installedPath);
        }

        DeleteIfEmpty(legacyToolsDir);
        DeleteIfEmpty(Path.GetDirectoryName(legacyToolsDir)!);
    }

    private async Task<string?> MigrateNativeAsync(ToolManifestEntry tool, string legacyToolsDir, CancellationToken cancellationToken)
    {
        // The old installer copied the GitHub clone folder (tool-<name>) or the local file into legacyToolsDir.
        var legacyCopy = tool.Source == SkillSource.GitHub || string.IsNullOrWhiteSpace(tool.LocalPath)
            ? Path.Combine(legacyToolsDir, $"tool-{tool.Name}")
            : Path.Combine(legacyToolsDir, Path.GetFileName(tool.LocalPath));

        var source = new[]
            {
                tool.Source == SkillSource.GitHub ? Path.Combine(_paths.SkillCacheDirectory, $"tool-{tool.Name}") : tool.LocalPath,
                legacyCopy
            }
            .FirstOrDefault(path => path is not null && InstalledFiles.Exists(path));

        if (source is null)
        {
            LogToolNotMigrated(tool.Name, "its source is gone");
            return null;
        }

        var result = await _installer.InstallNativeAsync(tool.Name, source, InstallTarget.Global, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            LogToolNotMigrated(tool.Name, result.Error.Description);
            return null;
        }

        if (InstalledFiles.Exists(legacyCopy))
            InstalledFiles.Delete(legacyCopy);

        return result.Value;
    }

    private async Task<string?> MigrateMcpAsync(ToolManifestEntry tool, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tool.Command))
            return null;

        // With no recorded path, uninstalling removes the server from the legacy key.
        var removed = await _installer.UninstallAsync(tool, cancellationToken).ConfigureAwait(false);
        if (removed.IsFailure)
        {
            LogToolNotMigrated(tool.Name, removed.Error.Description);
            return null;
        }

        var result = await _installer.InstallMcpAsync(tool.Name, tool.Command, tool.Args, tool.Env, InstallTarget.Global, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            LogToolNotMigrated(tool.Name, result.Error.Description);
            return null;
        }

        return result.Value;
    }

    /// <summary>
    /// Removes Fleet's global installs of the retired catalog entries, as Settings would. An entry stays when its
    /// files couldn't be deleted, so the next start tries again. Runs after the moves, so it also removes copies they
    /// adopted; a file Fleet didn't write stays where it is.
    /// </summary>
    internal async Task RemoveRetiredAsync(CancellationToken cancellationToken)
    {
        var skills = await _skillStore.LoadAsync(LocalOwnerUserId, workspaceId: null, cancellationToken).ConfigureAwait(false);
        foreach (var skill in skills.Skills.Where(s => IsRetired(RetiredSkills, s.Name, s.Source, s.RepoUrl, s.Scope)))
        {
            var problems = (await _syncEngine.RemoveSkillAsync(skill, cancellationToken).ConfigureAwait(false))
                .Where(r => !r.Success)
                .Select(r => r.ErrorMessage ?? "Unknown error")
                .ToList();
            if (problems.Count > 0)
            {
                LogRetiredNotRemoved(skill.Name, string.Join(" ", problems));
                continue;
            }

            await _skillStore.RemoveEntryAsync(skills.UserId, skills.WorkspaceId, skill.Name, InstallTarget.Global, cancellationToken)
                .ConfigureAwait(false);
            LogRetiredRemoved(skill.Name);
        }

        var tools = await _toolStore.LoadAsync(LocalOwnerUserId, workspaceId: null, cancellationToken).ConfigureAwait(false);
        var legacyToolsDir = Path.Combine(_paths.HomeDirectory, ".config", "weave-fleet", "tools", tools.UserId);
        foreach (var tool in tools.Tools.Where(t => IsRetired(RetiredTools, t.Name, t.Source, t.RepoUrl, t.Scope)))
        {
            var removed = await _installer.UninstallAsync(tool, cancellationToken).ConfigureAwait(false);
            if (removed.IsFailure)
            {
                LogRetiredNotRemoved(tool.Name, removed.Error.Description);
                continue;
            }

            // A copy the moves couldn't place still sits in the old folder, which only Fleet used.
            var legacyCopy = Path.Combine(legacyToolsDir, $"tool-{tool.Name}");
            if (InstalledFiles.Exists(legacyCopy))
                InstalledFiles.Delete(legacyCopy);

            await _toolStore.RemoveEntryAsync(tools.UserId, tools.WorkspaceId, tool.Name, InstallTarget.Global, cancellationToken)
                .ConfigureAwait(false);
            LogRetiredRemoved(tool.Name);
        }

        DeleteIfEmpty(legacyToolsDir);
        DeleteIfEmpty(Path.GetDirectoryName(legacyToolsDir)!);
    }

    private static bool IsRetired(string[] retired, string name, SkillSource source, string? repoUrl, InstallScope scope) =>
        scope == InstallScope.Global
        && source == SkillSource.GitHub
        && retired.Contains(name, StringComparer.Ordinal)
        && string.Equals(repoUrl?.TrimEnd('/'), FleetRepoUrl, StringComparison.OrdinalIgnoreCase);

    private static void DeleteIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved skill '{SkillName}' to a copy in its harness folders.")]
    private partial void LogSkillMigrated(string skillName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Left skill '{SkillName}' where it was: {Reason}")]
    private partial void LogSkillNotMigrated(string skillName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved tool '{ToolName}' to {InstalledPath}.")]
    private partial void LogToolMigrated(string toolName, string installedPath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Left tool '{ToolName}' where it was: {Reason}")]
    private partial void LogToolNotMigrated(string toolName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the old catalog install of '{Name}'.")]
    private partial void LogRetiredRemoved(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove the catalog install of '{Name}': {Reason}")]
    private partial void LogRetiredNotRemoved(string name, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Moving skills and tools from older Fleet install locations failed; startup continues.")]
    private partial void LogFailed(Exception ex);
}
