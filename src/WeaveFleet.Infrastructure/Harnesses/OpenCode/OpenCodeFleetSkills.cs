namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// The skills Fleet gives pooled sessions. Their sources are in the repo, embedded here:
/// <list type="bullet">
/// <item><c>opencode/skills</c>: skills every session gets, such as <c>fleet</c>.</item>
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

    /// <summary>Each embedded skill file under <paramref name="prefix"/>: its path under the skills folder and its resource name.</summary>
    internal static IEnumerable<(string RelativePath, string ResourceName)> Resources(string prefix = ResourcePrefix) =>
        typeof(OpenCodeFleetSkills).Assembly.GetManifestResourceNames()
            // RecursiveDir puts backslashes in the resource names when Fleet is built on Windows.
            .Select(name => (Normalized: name.Replace('\\', '/'), Name: name))
            .Where(resource => resource.Normalized.StartsWith(prefix, StringComparison.Ordinal))
            .Select(resource => (Path.Combine(resource.Normalized[prefix.Length..].Split('/')), resource.Name));

    /// <summary>
    /// The variable with the folders of the owner's own versions of built-in skills, separated like <c>PATH</c>. Each
    /// folder is one version, so a new version changes the pool key and new sessions get a process that loads it.
    /// </summary>
    internal const string YourVersionsVariable = "FLEET_YOUR_SKILLS";

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

        BuiltInSkillFiles.RemoveEmptyFolders(root);
        return root;
    }
}
