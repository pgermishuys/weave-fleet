using System.Security.Cryptography;
using System.Text;
using WeaveFleet.Application.Weave;

namespace WeaveFleet.Infrastructure.Weave;

/// <summary>
/// The folders Fleet writes Weave configs into, under <c>{data}/weave-config/</c>. Weave reads them through
/// <see cref="WeaveEnvironment"/>; nothing else writes to them.
/// </summary>
internal static class WeaveConfigFolder
{
    public static string Root(string dataDirectory) => Path.Combine(dataDirectory, "weave-config");

    /// <summary>The folder a user's saved config lives in. Its path never changes, so a save doesn't change a process's environment.</summary>
    public static string ForUser(string dataDirectory, string userId) =>
        Path.Combine(Root(dataDirectory), UserKey(userId));

    /// <summary>Where a harness tries drafts and probes for a user, apart from the saved config.</summary>
    public static string TrialFor(string dataDirectory, string userId) =>
        Path.Combine(Root(dataDirectory), UserKey(userId) + "-trial");

    /// <summary>An empty folder a harness lists agents in while it tries a draft; Weave writes its log under it.</summary>
    public static string TrialProjectFor(string dataDirectory, string userId) =>
        Path.Combine(Root(dataDirectory), UserKey(userId) + "-trial-project");

    private static string UserKey(string userId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16];

    /// <summary>
    /// Makes <paramref name="folder"/> hold exactly <paramref name="files"/>: changed files are replaced atomically,
    /// and config and prompt files that aren't in <paramref name="files"/> are deleted. Unchanged files aren't touched.
    /// </summary>
    public static void Mirror(string folder, IReadOnlyDictionary<string, string> files)
    {
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);

        foreach (var (relative, content) in files)
        {
            if (WeaveConfigFiles.RejectPath(relative) is { } rejected)
                throw new ArgumentException(rejected, nameof(files));

            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException($"'{relative}' is outside the Weave config folder.", nameof(files));

            if (File.Exists(path) && File.ReadAllText(path) == content)
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }

        foreach (var existing in ExistingFiles(root))
        {
            if (!files.ContainsKey(existing))
                File.Delete(Path.Combine(root, existing));
        }
    }

    private static IEnumerable<string> ExistingFiles(string root)
    {
        foreach (var name in new[] { WeaveConfigFiles.Config, WeaveConfigFiles.LegacyConfig })
        {
            if (File.Exists(Path.Combine(root, name)))
                yield return name;
        }

        var prompts = Path.Combine(root, WeaveConfigFiles.PromptsFolder.TrimEnd('/'));
        if (!Directory.Exists(prompts))
            yield break;

        foreach (var file in Directory.EnumerateFiles(prompts, "*.md"))
            yield return WeaveConfigFiles.PromptsFolder + Path.GetFileName(file);
    }
}
