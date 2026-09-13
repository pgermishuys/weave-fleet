using WeaveFleet.Domain.Skills;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Where each harness looks for skills, tools and config, for the user and inside a repository.
/// </summary>
/// <remarks>
/// OpenCode resolves its global folder with xdg-basedir: <c>$XDG_CONFIG_HOME/opencode</c>, else
/// <c>~/.config/opencode</c>. That holds on Windows too (<c>%USERPROFILE%\.config\opencode</c>);
/// xdg-basedir has no Windows special case, so neither do we.
/// </remarks>
public sealed class HarnessInstallPaths
{
    public const string OpenCode = "opencode";
    public const string ClaudeCode = "claude-code";

    public HarnessInstallPaths(string homeDirectory, string? xdgConfigHome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        HomeDirectory = homeDirectory;
        var configHome = !string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome)
            ? xdgConfigHome
            : Path.Combine(homeDirectory, ".config");

        OpenCodeGlobalDirectory = Path.Combine(configHome, "opencode");
        ClaudeCodeGlobalDirectory = Path.Combine(homeDirectory, ".claude");
        SkillCacheDirectory = Path.Combine(homeDirectory, ".weave", "skills");
    }

    public static HarnessInstallPaths FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));

    /// <summary>Harnesses a skill can be installed for.</summary>
    public static IReadOnlyList<string> SkillHarnesses { get; } = [OpenCode, ClaudeCode];

    public string HomeDirectory { get; }

    public string OpenCodeGlobalDirectory { get; }

    public string ClaudeCodeGlobalDirectory { get; }

    /// <summary>Fleet's own copy of each skill's source (GitHub clones), under ~/.weave/skills.</summary>
    public string SkillCacheDirectory { get; }

    /// <summary>
    /// The harness's folder for a target: <c>~/.config/opencode</c> or <c>&lt;repo&gt;/.opencode</c>,
    /// <c>~/.claude</c> or <c>&lt;repo&gt;/.claude</c>. Null for a harness that has no skills folder.
    /// </summary>
    public string? HarnessDirectory(string harness, InstallTarget target)
    {
        var (global, projectFolder) = harness.ToLowerInvariant() switch
        {
            OpenCode => (OpenCodeGlobalDirectory, ".opencode"),
            ClaudeCode => (ClaudeCodeGlobalDirectory, ".claude"),
            _ => (null, null)
        };

        if (global is null)
            return null;

        return target.Scope == InstallScope.Global
            ? global
            : Path.Combine(target.ProjectPath!, projectFolder!);
    }

    /// <summary>The folder a skill is copied to, e.g. <c>~/.config/opencode/skills/&lt;name&gt;</c>.</summary>
    public string? SkillDirectory(string harness, InstallTarget target, string skillName) =>
        HarnessDirectory(harness, target) is { } dir ? Path.Combine(dir, "skills", skillName) : null;

    /// <summary>OpenCode's custom tools folder: <c>~/.config/opencode/tools</c> or <c>&lt;repo&gt;/.opencode/tools</c>.</summary>
    public string OpenCodeToolsDirectory(InstallTarget target) =>
        Path.Combine(HarnessDirectory(OpenCode, target)!, "tools");

    /// <summary>
    /// The folder whose <c>opencode.json</c> holds MCP servers: the global OpenCode folder, or the repository root.
    /// </summary>
    public string OpenCodeConfigDirectory(InstallTarget target) =>
        target.Scope == InstallScope.Global ? OpenCodeGlobalDirectory : target.ProjectPath!;
}
