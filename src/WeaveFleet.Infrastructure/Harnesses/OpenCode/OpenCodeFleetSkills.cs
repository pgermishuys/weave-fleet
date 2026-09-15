namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// The skills Fleet gives pooled sessions, such as <c>fleet-api</c>. Their sources are under <c>opencode/skills</c> in
/// the repo, embedded here. Fleet writes them into its data folder and passes that folder to the Fleet plugin
/// (<see cref="OpenCodeFleetPlugin"/>), which adds it to OpenCode's <c>skills.paths</c>. The user's own OpenCode folders
/// and skill paths are left alone.
/// </summary>
internal static class OpenCodeFleetSkills
{
    /// <summary>The variable that tells the Fleet plugin where the skills are.</summary>
    internal const string PathVariable = "FLEET_SKILLS_PATH";

    private const string ResourcePrefix = "opencode/skills/";

    /// <summary>
    /// Writes the skills to <c>{dataDirectory}/opencode/skills</c>, leaving identical copies alone, deletes files there
    /// that Fleet no longer ships, and returns the folder.
    /// </summary>
    public static string Install(string dataDirectory)
    {
        var root = Path.Combine(dataDirectory, "opencode", "skills");
        var shipped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (relativePath, resourceName) in Resources())
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

    /// <summary>Each embedded skill file: its path under the skills folder and its resource name.</summary>
    internal static IEnumerable<(string RelativePath, string ResourceName)> Resources() =>
        typeof(OpenCodeFleetSkills).Assembly.GetManifestResourceNames()
            // RecursiveDir puts backslashes in the resource names when Fleet is built on Windows.
            .Select(name => (Normalized: name.Replace('\\', '/'), Name: name))
            .Where(resource => resource.Normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(resource => (Path.Combine(resource.Normalized[ResourcePrefix.Length..].Split('/')), resource.Name));
}
