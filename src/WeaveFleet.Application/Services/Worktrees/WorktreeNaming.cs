using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Services.Worktrees;

/// <summary>The templates that name a new worktree's branch and folder.</summary>
public sealed record WorktreeNaming
{
    /// <summary>What Fleet named worktrees before the templates existed.</summary>
    public static readonly WorktreeNaming Defaults = new();

    public const string DefaultBranch = "fleet/{slug}";
    public const string DefaultRoot = "{repoParent}/{repo}-worktrees";
    public const string DefaultFolder = "{branch}";

    public string Branch { get; init; } = DefaultBranch;
    public string Root { get; init; } = DefaultRoot;
    public string Folder { get; init; } = DefaultFolder;

    /// <summary>Named regexes read from the message, each one a token of its own.</summary>
    public IReadOnlyDictionary<string, string> Capture { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>What <c>{initials}</c> resolves to. Explicit — it can't be derived from a username.</summary>
    public string? Initials { get; init; }

    /// <summary>
    /// Overlays <paramref name="over"/>'s set fields on this one, field by field, so a repository
    /// that names only a branch leaves the user's root and folder alone.
    /// </summary>
    public WorktreeNaming Overlay(WorktreeNamingOverride over) => new()
    {
        Branch = over.Branch ?? Branch,
        Root = over.Root ?? Root,
        Folder = over.Folder ?? Folder,
        Capture = over.Capture ?? Capture,
        Initials = over.Initials ?? Initials,
    };
}

/// <summary>
/// One layer's contribution: only the fields it actually sets. Null means "defer to the layer below".
/// </summary>
public sealed record WorktreeNamingOverride
{
    public static readonly WorktreeNamingOverride Empty = new();

    public string? Branch { get; init; }
    public string? Root { get; init; }
    public string? Folder { get; init; }
    public IReadOnlyDictionary<string, string>? Capture { get; init; }
    public string? Initials { get; init; }

    public bool IsEmpty => Branch is null && Root is null && Folder is null && Capture is null && Initials is null;
}

/// <summary>Which layer a field's effective value came from, for Settings to show.</summary>
public enum WorktreeNamingLayer
{
    Default,
    User,
    Project,
}

/// <summary>The effective templates plus where each field came from.</summary>
public sealed record ResolvedWorktreeNaming(
    WorktreeNaming Effective,
    IReadOnlyDictionary<string, WorktreeNamingLayer> Layers);

/// <summary>
/// The facts a template resolves against, other than the message. <paramref name="ShortId"/> is
/// passed in rather than generated so a preview and the worktree it previews can agree.
/// </summary>
public sealed record WorktreeNamingContext(
    string RepositoryPath,
    string UserName,
    string? Initials,
    DateOnly Date,
    string ShortId,
    string HomeDirectory);

/// <summary>A resolved name: the branch, or null when the server should name it instead.</summary>
public sealed record WorktreeNameResult(string? Branch, string Root, string Folder)
{
    /// <summary>True when the template wanted a slug and the message gave none.</summary>
    public bool NeedsServerName => Branch is null;
}

/// <summary>A template that can't be used, and why — reported by Settings, not by git.</summary>
public sealed record WorktreeNamingProblem(string Field, string Message)
{
    public FleetError ToError() => FleetError.ValidationError($"Worktrees.{Field}", Message);
}
