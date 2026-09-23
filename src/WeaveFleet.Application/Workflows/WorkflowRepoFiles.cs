using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WeaveFleet.Domain.Common;

namespace WeaveFleet.Application.Workflows;

/// <summary>A repository's workflow file as the editor opens it.</summary>
/// <param name="File">The repo-relative path, e.g. <c>.weave/workflows/deps.yaml</c>.</param>
/// <param name="Hash">The file's bytes as read, hashed: a save is refused if the file has changed since.</param>
public sealed record WorkflowFileOpened(string WorkflowId, string File, string Hash, WorkflowCheckResult Check);

/// <summary>
/// Reads, saves and creates the files in a repository's <c>.weave/workflows/</c>, and nothing outside it. Saving is an
/// ordinary change in the user's checkout: Fleet doesn't commit it.
/// </summary>
public static partial class WorkflowRepoFiles
{
    /// <summary>Longer than any sensible name; the file name stays readable in a tree view.</summary>
    private const int MaxSlugLength = 60;

    public const string ChangedOnDiskMessage =
        "This file changed on disk since you opened it. Reload to see the change, or keep yours and save over it.";

    public static async Task<Result<WorkflowFileOpened>> OpenAsync(string repository, string workflowId, CancellationToken ct)
    {
        var path = Resolve(repository, workflowId);
        if (path.IsFailure)
            return path.Error;

        var bytes = await File.ReadAllBytesAsync(path.Value.Full, ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        return new WorkflowFileOpened(workflowId, path.Value.Relative, Hash(bytes), WorkflowCheck.Text(text, path.Value.Relative));
    }

    /// <summary>
    /// Saves <paramref name="text"/> over the file, exactly as given, once the parser takes it. Refused when the file
    /// isn't what was opened (<paramref name="openedHash"/>), unless <paramref name="force"/>: Keep mine.
    /// </summary>
    public static async Task<Result<WorkflowFileOpened>> SaveAsync(
        string repository, string workflowId, string text, string? openedHash, bool force, CancellationToken ct)
    {
        var path = Resolve(repository, workflowId, mustExist: false);
        if (path.IsFailure)
            return path.Error;

        var check = WorkflowCheck.Text(text, path.Value.Relative);
        if (!check.IsValid)
        {
            var first = check.Errors[0];
            return FleetError.ValidationError("Text", first.Line > 0 ? $"Line {first.Line}: {first.Message}" : first.Message);
        }

        if (!force)
        {
            var current = File.Exists(path.Value.Full) ? Hash(await File.ReadAllBytesAsync(path.Value.Full, ct).ConfigureAwait(false)) : null;
            if (current != openedHash)
                return new FleetError("General.Conflict", ChangedOnDiskMessage);
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        await WriteAsync(path.Value.Full, bytes, ct).ConfigureAwait(false);
        return new WorkflowFileOpened(workflowId, path.Value.Relative, Hash(bytes), check);
    }

    /// <summary>
    /// Creates <c>.weave/workflows/&lt;slug&gt;.yaml</c> with <paramref name="workflow"/> written in Fleet's layout.
    /// Refused when a file with that name is there, or the library already has a workflow called that.
    /// </summary>
    public static async Task<Result<WorkflowFileOpened>> CreateAsync(
        string repository, WorkflowDefinition workflow, IReadOnlyList<WorkflowEntry> library, CancellationToken ct)
    {
        var name = workflow.Name.Trim();
        if (name.Length == 0)
            return FleetError.ValidationError("Name", "Give the workflow a name.");

        var taken = library.FirstOrDefault(entry => string.Equals(entry.Definition?.Name ?? entry.Name, name, StringComparison.OrdinalIgnoreCase));
        if (taken is not null)
            return new FleetError("General.Conflict", $"There's already a workflow called {taken.Definition?.Name ?? taken.Name}. Pick another name.");

        var slug = Slug(name);
        var folder = Path.Combine(repository, WorkflowCatalog.RepoFolder);
        if (File.Exists(Path.Combine(folder, slug + ".yaml")) || File.Exists(Path.Combine(folder, slug + ".yml")))
            return new FleetError("General.Conflict", $"There's already a file called {slug}.yaml in .weave/workflows. Pick another name.");

        Directory.CreateDirectory(folder);
        var id = WorkflowCatalog.RepoPrefix + slug;
        var path = Resolve(repository, id, mustExist: false);
        if (path.IsFailure)
            return path.Error;

        var text = WorkflowYamlWriter.Write(workflow with { Name = name });
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            // CreateNew: a file that appeared since the check above is never overwritten.
            await using var stream = new FileStream(path.Value.Full, FileMode.CreateNew, FileAccess.Write);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(path.Value.Full))
        {
            return new FleetError("General.Conflict", $"There's already a file called {slug}.yaml in .weave/workflows. Pick another name.");
        }

        return new WorkflowFileOpened(id, path.Value.Relative, Hash(bytes), WorkflowCheck.Text(text, path.Value.Relative));
    }

    /// <summary>The smallest workflow that runs: New workflow starts from it.</summary>
    public static WorkflowDefinition Blank(string name) => new(
        name,
        null,
        null,
        WorkflowStarts.Sentence,
        WorkflowPlaces.NewWorktree,
        [
            new WorkflowAgentStep(
                "work", "Do the work", 0, "build", WorkflowRoles.Standard, null, null, false, null,
                "The request: {{request}}\n", ["done"], new Dictionary<string, string>(), null, null, []),
        ]);

    /// <summary>"Tidy up a flaky test" → <c>tidy-up-a-flaky-test</c>.</summary>
    public static string Slug(string name)
    {
        var slug = NotSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > MaxSlugLength)
            slug = slug[..MaxSlugLength].TrimEnd('-');
        return slug.Length > 0 ? slug : "workflow";
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// The file a workflow id names: <c>repo:&lt;stem&gt;</c> is <c>.weave/workflows/&lt;stem&gt;.yaml</c> (or
    /// <c>.yml</c>), and only if its real path, links followed, is in that folder of this repository.
    /// </summary>
    private static Result<(string Full, string Relative)> Resolve(string repository, string workflowId, bool mustExist = true)
    {
        if (workflowId.StartsWith(WorkflowCatalog.BuiltInPrefix, StringComparison.Ordinal))
            return FleetError.ValidationError("Workflow", "Built-in workflows can't be edited. Duplicate it into this repo to change it.");

        var stem = workflowId.StartsWith(WorkflowCatalog.RepoPrefix, StringComparison.Ordinal) ? workflowId[WorkflowCatalog.RepoPrefix.Length..] : string.Empty;
        if (!StemPattern().IsMatch(stem))
            return FleetError.ValidationError("Workflow", "That isn't a workflow file in .weave/workflows.");

        var folder = Path.GetFullPath(Path.Combine(repository, WorkflowCatalog.RepoFolder));
        var yaml = Path.Combine(folder, stem + ".yaml");
        var yml = Path.Combine(folder, stem + ".yml");
        var full = File.Exists(yaml) || !File.Exists(yml) ? yaml : yml;
        if (mustExist && !File.Exists(full))
            return FleetError.NotFoundFor("Workflow", workflowId);

        // A folder or a file that's a link could point anywhere; follow them and look where they land.
        var repoRoot = Real(Path.GetFullPath(repository));
        var realFolder = Real(folder);
        var expected = Path.TrimEndingDirectorySeparator(Path.Combine(repoRoot, WorkflowCatalog.RepoFolder));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(realFolder), expected, StringComparison.Ordinal))
            return FleetError.ValidationError("Workflow", ".weave/workflows is a link out of the repository, so Fleet won't write there.");
        if (File.Exists(full) && new FileInfo(full).LinkTarget is not null)
            return FleetError.ValidationError("Workflow", $"{Path.GetFileName(full)} is a link, so Fleet won't write through it.");

        return (full, Path.Combine(WorkflowCatalog.RepoFolder, Path.GetFileName(full)).Replace('\\', '/'));
    }

    /// <summary>The path with every link in it followed, as far as the path exists.</summary>
    private static string Real(string path)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        if (Directory.Exists(path))
            return ResolveEach(path);
        return Path.GetDirectoryName(path) is { } parent ? Path.Combine(Real(parent), Path.GetFileName(path)) : path;
    }

    /// <summary>Follows a link at every level of an existing folder's path.</summary>
    private static string ResolveEach(string folder)
    {
        var root = Path.GetPathRoot(folder) ?? string.Empty;
        var real = root;
        foreach (var part in folder[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = Path.Combine(real, part);
            real = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: true) is { } target ? ResolveEach(target.FullName) : next;
        }

        return real;
    }

    /// <summary>Through a temporary file next to it, so a reader never sees half a file.</summary>
    private static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._ -]*$")]
    private static partial Regex StemPattern();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
