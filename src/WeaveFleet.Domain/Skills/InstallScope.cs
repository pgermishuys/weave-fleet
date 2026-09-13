namespace WeaveFleet.Domain.Skills;

/// <summary>
/// Where a skill or tool is installed.
/// </summary>
public enum InstallScope
{
    /// <summary>The user's harness config (e.g. ~/.config/opencode), seen by every session.</summary>
    Global,

    /// <summary>A repository's harness folder (e.g. &lt;repo&gt;/.opencode), seen by sessions in that repository.</summary>
    Project
}

/// <summary>
/// An install scope, plus the repository path when the scope is <see cref="InstallScope.Project"/>.
/// </summary>
public sealed record InstallTarget
{
    public static readonly InstallTarget Global = new(InstallScope.Global, null);

    private InstallTarget(InstallScope scope, string? projectPath)
    {
        Scope = scope;
        ProjectPath = projectPath;
    }

    public InstallScope Scope { get; }

    /// <summary>The repository root. Set only for <see cref="InstallScope.Project"/>.</summary>
    public string? ProjectPath { get; }

    public static InstallTarget Project(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return new InstallTarget(InstallScope.Project, projectPath);
    }

    /// <summary>Rebuilds a target from the fields stored on a manifest entry.</summary>
    public static InstallTarget From(InstallScope scope, string? projectPath) =>
        scope == InstallScope.Project && !string.IsNullOrWhiteSpace(projectPath)
            ? Project(projectPath)
            : Global;

    public bool Matches(InstallScope scope, string? projectPath) =>
        Equals(From(scope, projectPath));
}
