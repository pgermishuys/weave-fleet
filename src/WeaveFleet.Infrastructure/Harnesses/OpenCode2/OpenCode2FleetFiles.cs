using System.Buffers;
using System.Text;
using System.Text.Json;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// What Fleet gives the OpenCode 2 servers it starts, embedded in Fleet and written into its data folder under
/// <c>opencode2/</c>:
/// <list type="bullet">
/// <item>The Fleet plugin (<c>opencode2/fleet/index.js</c> in the repo): the canvas, app and browser tools. V2 loads a
/// plugin from a folder, so it's written as <c>fleet/index.js</c> and the folder is what the config names.</item>
/// <item>Fleet's skills: <c>opencode/skills</c> (such as <c>fleet-api</c>) for every session, and
/// <c>opencode/built-in-skills</c>, one folder per skill, for the ones the owner turned on. They're the same skill
/// files the OpenCode harness installs, with a copy of their own here, so neither harness depends on the other.</item>
/// </list>
/// The server loads them through <c>OPENCODE_CONFIG_CONTENT</c> (<see cref="BuildConfigContent"/>). V2 adds its
/// <c>plugins</c> and <c>skills</c> to the user's own, so the user's plugins and skill folders still load.
/// </summary>
internal static class OpenCode2FleetFiles
{
    internal const string PluginResource = "opencode2/fleet/index.js";
    internal const string SkillsPrefix = "opencode/skills/";
    internal const string BuiltInSkillsPrefix = "opencode/built-in-skills/";

    /// <summary>Writes the plugin to <c>{dataDirectory}/opencode2/fleet/index.js</c> and returns its folder.</summary>
    public static string InstallPlugin(string dataDirectory)
    {
        var folder = Path.Combine(dataDirectory, "opencode2", "fleet");
        WriteIfChanged(Path.Combine(folder, "index.js"), Read(PluginResource));
        return folder;
    }

    /// <summary>Writes the skills every session gets to <c>{dataDirectory}/opencode2/skills</c> and returns the folder.</summary>
    public static string InstallSkills(string dataDirectory) =>
        InstallFolder(SkillsPrefix, Path.Combine(dataDirectory, "opencode2", "skills"));

    /// <summary>
    /// Writes the built-in skills to <c>{dataDirectory}/opencode2/built-in-skills</c>, one folder per skill, and returns
    /// the folder.
    /// </summary>
    public static string InstallBuiltInSkills(string dataDirectory) =>
        InstallFolder(BuiltInSkillsPrefix, Path.Combine(dataDirectory, "opencode2", "built-in-skills"));

    /// <summary>The names of the built-in skills this Fleet ships: the folders under <c>opencode/built-in-skills</c>.</summary>
    public static IReadOnlySet<string> BuiltInSkillNames { get; } = Resources(BuiltInSkillsPrefix)
        .Select(resource => resource.RelativePath.Split(Path.DirectorySeparatorChar)[0])
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The config Fleet adds to the user's: <c>{"plugins": [...], "skills": [...]}</c>, or <see langword="null"/> when
    /// there's nothing to add. Permissions aren't here: every session is created with an allow-all ruleset.
    /// </summary>
    public static string? BuildConfigContent(string? pluginFolder, IReadOnlyList<string> skillFolders)
    {
        if (pluginFolder is null && skillFolders.Count == 0)
            return null;

        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            if (pluginFolder is not null)
            {
                json.WriteStartArray("plugins");
                json.WriteStringValue(pluginFolder);
                json.WriteEndArray();
            }

            if (skillFolders.Count > 0)
            {
                json.WriteStartArray("skills");
                foreach (var folder in skillFolders)
                    json.WriteStringValue(folder);
                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static byte[] Read(string resourceName)
    {
        using var stream = typeof(OpenCode2FleetFiles).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource {resourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Each embedded file under <paramref name="prefix"/>: its path under the folder and its resource name.</summary>
    internal static IEnumerable<(string RelativePath, string ResourceName)> Resources(string prefix) =>
        typeof(OpenCode2FleetFiles).Assembly.GetManifestResourceNames()
            // RecursiveDir puts backslashes in the resource names when Fleet is built on Windows.
            .Select(name => (Normalized: name.Replace('\\', '/'), Name: name))
            .Where(resource => resource.Normalized.StartsWith(prefix, StringComparison.Ordinal))
            .Select(resource => (Path.Combine(resource.Normalized[prefix.Length..].Split('/')), resource.Name));

    /// <summary>Writes the files under <paramref name="prefix"/> into <paramref name="root"/> and deletes files Fleet no longer ships.</summary>
    private static string InstallFolder(string prefix, string root)
    {
        var shipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relativePath, resourceName) in Resources(prefix))
        {
            var path = Path.GetFullPath(Path.Combine(root, relativePath));
            WriteIfChanged(path, Read(resourceName));
            shipped.Add(path);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!shipped.Contains(Path.GetFullPath(file)))
                File.Delete(file);
        }

        return root;
    }

    /// <summary>
    /// Writes <paramref name="content"/> unless an identical copy is already there, through a temp file, so a starting
    /// server never reads half a file.
    /// </summary>
    private static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
