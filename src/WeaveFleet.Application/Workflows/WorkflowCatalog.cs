namespace WeaveFleet.Application.Workflows;

/// <summary>A workflow the library lists: its file, and the workflow or what's wrong with it.</summary>
/// <param name="Id"><c>builtin:&lt;name&gt;</c> or <c>repo:&lt;file name without .yaml&gt;</c>.</param>
/// <param name="File">Where it lives: the repo-relative path, or null for a built-in.</param>
/// <param name="Text">The file as it is now; a run keeps its own copy.</param>
public sealed record WorkflowEntry(
    string Id,
    string? File,
    string Text,
    WorkflowDefinition? Definition,
    IReadOnlyList<WorkflowFileError> Errors)
{
    public bool IsBuiltIn => Id.StartsWith(WorkflowCatalog.BuiltInPrefix, StringComparison.Ordinal);
}

/// <summary>
/// The workflows Fleet can run in a repository: the built-ins it ships, then the repository's own
/// <c>.weave/workflows/*.yaml</c>. A file that doesn't read comes back with its errors; the rest still load.
/// </summary>
public static class WorkflowCatalog
{
    public const string BuiltInPrefix = "builtin:";
    public const string RepoPrefix = "repo:";

    /// <summary>Where a repository keeps its own workflows.</summary>
    public static readonly string RepoFolder = Path.Combine(".weave", "workflows");

    // Enough for any hand-written workflow; a bigger file is a mistake, and Fleet reads these on every list.
    private const long MaxFileBytes = 64 * 1024;
    private const int MaxFiles = 50;

    private static readonly Lazy<List<WorkflowEntry>> BuiltInEntries = new(LoadBuiltIns);

    public static IReadOnlyList<WorkflowEntry> BuiltIns => BuiltInEntries.Value;

    /// <summary>The built-ins, then the repository's own workflows by file name. The path must already be checked.</summary>
    public static async Task<IReadOnlyList<WorkflowEntry>> ListAsync(string? repositoryPath, CancellationToken ct = default)
    {
        var entries = new List<WorkflowEntry>(BuiltIns);
        if (string.IsNullOrWhiteSpace(repositoryPath))
            return entries;

        var folder = Path.Combine(repositoryPath, RepoFolder);
        if (!Directory.Exists(folder))
            return entries;

        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Take(MaxFiles);

        foreach (var path in files)
            entries.Add(await ReadRepoFileAsync(path, ct).ConfigureAwait(false));

        return entries;
    }

    /// <summary>The workflow with <paramref name="id"/>, or null when there's none.</summary>
    public static async Task<WorkflowEntry?> FindAsync(string id, string? repositoryPath, CancellationToken ct = default)
    {
        if (id.StartsWith(BuiltInPrefix, StringComparison.Ordinal))
            return BuiltIns.FirstOrDefault(entry => entry.Id == id);

        var entries = await ListAsync(repositoryPath, ct).ConfigureAwait(false);
        return entries.FirstOrDefault(entry => entry.Id == id);
    }

    private static async Task<WorkflowEntry> ReadRepoFileAsync(string path, CancellationToken ct)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var relative = Path.Combine(RepoFolder, Path.GetFileName(path)).Replace('\\', '/');
        var id = RepoPrefix + name;

        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                return new WorkflowEntry(id, relative, string.Empty, null, [new WorkflowFileError(relative, 0, "The file is over 64 KB; a workflow is a short list of steps.")]);

            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var parsed = WorkflowYaml.Parse(text, relative);
            return new WorkflowEntry(id, relative, text, parsed.Definition, parsed.Errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorkflowEntry(id, relative, string.Empty, null, [new WorkflowFileError(relative, 0, $"Fleet couldn't read it: {ex.Message}")]);
        }
    }

    private static List<WorkflowEntry> LoadBuiltIns()
    {
        var assembly = typeof(WorkflowCatalog).Assembly;
        var entries = new List<WorkflowEntry>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith("workflows/", StringComparison.Ordinal) && name.EndsWith(".yaml", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var name = Path.GetFileNameWithoutExtension(resource["workflows/".Length..]);
            var parsed = WorkflowYaml.Parse(text, $"built-in {name}.yaml");
            entries.Add(new WorkflowEntry(BuiltInPrefix + name, null, text, parsed.Definition, parsed.Errors));
        }

        return entries;
    }
}
