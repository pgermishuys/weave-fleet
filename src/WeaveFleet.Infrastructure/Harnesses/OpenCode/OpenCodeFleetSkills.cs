using WeaveFleet.Application.Skills;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// The skills Fleet gives pooled sessions. Their sources are in the repo, embedded here:
/// <list type="bullet">
/// <item><c>opencode/skills</c>: skills every session gets, such as <c>fleet-api</c>.</item>
/// <item><c>opencode/built-in-skills</c>: skills a session gets only when its owner turned them on in Settings.</item>
/// </list>
/// Fleet writes them into its data folder and passes the folders a process should load to the Fleet plugin
/// (<see cref="OpenCodeFleetPlugin"/>), which adds them to OpenCode's <c>skills.paths</c>. The user's own OpenCode folders
/// and skill paths are left alone.
/// </summary>
internal static class OpenCodeFleetSkills
{
    /// <summary>The variable that tells the Fleet plugin which folders to load skills from, separated like <c>PATH</c>.</summary>
    internal const string PathVariable = "FLEET_SKILLS_PATH";

    /// <summary>
    /// The built-in skills the session's owner turned on, comma-separated. It's set when the runtime is prepared, before the
    /// pool key is hashed, so sessions with different choices never share a process.
    /// </summary>
    internal const string BuiltInVariable = "FLEET_BUILT_IN_SKILLS";

    private const string ResourcePrefix = "opencode/skills/";
    private const string BuiltInResourcePrefix = "opencode/built-in-skills/";

    private static readonly Lazy<IReadOnlyList<BuiltInSkill>> _builtIn = new(ReadBuiltIn);

    /// <summary>
    /// Writes the skills every session gets to <c>{dataDirectory}/opencode/skills</c>, leaving identical copies alone,
    /// deletes files there that Fleet no longer ships, and returns the folder.
    /// </summary>
    public static string Install(string dataDirectory) =>
        Install(ResourcePrefix, Path.Combine(dataDirectory, "opencode", "skills"));

    /// <summary>
    /// Writes the built-in skills to <c>{dataDirectory}/opencode/built-in-skills</c> the same way, and returns the folder.
    /// Each skill is in a folder of its own name under it.
    /// </summary>
    public static string InstallBuiltIn(string dataDirectory) =>
        Install(BuiltInResourcePrefix, Path.Combine(dataDirectory, "opencode", "built-in-skills"));

    /// <summary>The built-in skills, with the name and description from each one's <c>SKILL.md</c>, in name order.</summary>
    public static IReadOnlyList<BuiltInSkill> BuiltIn => _builtIn.Value;

    /// <summary>Each embedded skill file under <paramref name="prefix"/>: its path under the skills folder and its resource name.</summary>
    internal static IEnumerable<(string RelativePath, string ResourceName)> Resources(string prefix = ResourcePrefix) =>
        typeof(OpenCodeFleetSkills).Assembly.GetManifestResourceNames()
            // RecursiveDir puts backslashes in the resource names when Fleet is built on Windows.
            .Select(name => (Normalized: name.Replace('\\', '/'), Name: name))
            .Where(resource => resource.Normalized.StartsWith(prefix, StringComparison.Ordinal))
            .Select(resource => (Path.Combine(resource.Normalized[prefix.Length..].Split('/')), resource.Name));

    /// <summary>
    /// The <c>name</c> and <c>description</c> lines of a skill's front matter. Fleet writes its own skills, so a plain
    /// line scan is enough; a test keeps them to single-line values.
    /// </summary>
    internal static BuiltInSkill? ParseFrontMatter(string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length == 0 || lines[0] != "---")
            return null;

        string? name = null, description = null;
        foreach (var line in lines.Skip(1).TakeWhile(line => line != "---"))
        {
            if (line.StartsWith("name:", StringComparison.Ordinal))
                name = line["name:".Length..].Trim();
            else if (line.StartsWith("description:", StringComparison.Ordinal))
                description = line["description:".Length..].Trim();
        }

        return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(description) ? null : new BuiltInSkill(name, description);
    }

    private static string Install(string prefix, string root)
    {
        var shipped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (relativePath, resourceName) in Resources(prefix))
        {
            var path = Path.GetFullPath(Path.Combine(root, relativePath));
            EmbeddedFiles.WriteIfChanged(path, EmbeddedFiles.Read(resourceName));
            shipped.Add(path);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!shipped.Contains(Path.GetFullPath(file)))
                File.Delete(file);
        }

        return root;
    }

    private static List<BuiltInSkill> ReadBuiltIn() =>
        Resources(BuiltInResourcePrefix)
            .Where(resource => Path.GetFileName(resource.RelativePath) == "SKILL.md")
            .Select(resource => ParseFrontMatter(System.Text.Encoding.UTF8.GetString(EmbeddedFiles.Read(resource.ResourceName)))
                ?? throw new InvalidOperationException($"{resource.RelativePath} has no name or description."))
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .ToList();
}

/// <summary>The built-in skills Fleet embeds, for Settings.</summary>
internal sealed class OpenCodeBuiltInSkillCatalog : IBuiltInSkillCatalog
{
    public IReadOnlyList<BuiltInSkill> Skills => OpenCodeFleetSkills.BuiltIn;
}
