using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// The built-in skills Fleet ships (<c>opencode/built-in-skills</c> in the repo, one folder per skill, embedded here) as
/// files on disk, for the harnesses that load skills from a folder: OpenCode 2 names the folder in its config, Claude
/// Code loads it as a plugin.
/// </summary>
internal static class BuiltInSkillFiles
{
    internal const string Prefix = "opencode/built-in-skills/";

    /// <summary>The names of the built-in skills this Fleet ships: the folders under <c>opencode/built-in-skills</c>.</summary>
    public static IReadOnlySet<string> Names { get; } = Resources(Prefix)
        .Select(resource => resource.RelativePath.Split(Path.DirectorySeparatorChar)[0])
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Makes <paramref name="root"/> hold exactly the built-in skills in <paramref name="enabled"/>, one folder per skill,
    /// leaving identical files alone. A skill in <paramref name="yours"/> gets the owner's version of its
    /// <c>SKILL.md</c> in place of Fleet's.
    /// </summary>
    public static void Sync(string root, IReadOnlyCollection<string> enabled, IReadOnlyDictionary<string, string>? yours = null)
    {
        Directory.CreateDirectory(root);

        var shipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relativePath, resourceName) in Resources(Prefix))
        {
            var skill = relativePath.Split(Path.DirectorySeparatorChar)[0];
            if (!enabled.Contains(skill))
                continue;

            var path = Path.GetFullPath(Path.Combine(root, relativePath));
            WriteIfChanged(path, yours?.GetValueOrDefault(skill) is { } version && relativePath == Path.Combine(skill, "SKILL.md")
                ? Encoding.UTF8.GetBytes(version)
                : Read(resourceName));
            shipped.Add(path);
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!shipped.Contains(Path.GetFullPath(file)))
                File.Delete(file);
        }

        foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(f => f.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
    }

    /// <summary>
    /// The built-in skills the owner turned on that this Fleet ships, in name order, and the text of their own version of
    /// any they made one of. <paramref name="services"/> is a scope running as the owner. A version that can't be read is
    /// reported to <paramref name="unreadable"/> and left out, so Fleet's copy is used.
    /// </summary>
    public static async Task<OwnerSkills> ReadOwnerAsync(IServiceProvider services, string ownerUserId, Action<Exception>? unreadable = null)
    {
        if (services.GetService<IUserPreferenceRepository>() is not { } preferences)
            return OwnerSkills.None;

        var skills = await BuiltInSkillService.GetSessionSkillsAsync(
            preferences,
            services.GetService<ISkillVersionStore>(),
            ownerUserId,
            Names).ConfigureAwait(false);
        var yours = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _, folder) in skills)
        {
            if (folder is null)
                continue;

            try
            {
                yours[name] = File.ReadAllText(Path.Combine(folder, name, "SKILL.md"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable?.Invoke(ex);
            }
        }

        return new OwnerSkills(skills.Select(skill => skill.Name).ToList(), yours);
    }

    /// <summary>The owner's folder name: a short hash, since an owner id can be anything.</summary>
    internal static string OwnerFolder(string ownerUserId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ownerUserId)))[..16];

    internal static byte[] Read(string resourceName)
    {
        using var stream = typeof(BuiltInSkillFiles).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource {resourceName} is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Each embedded file under <paramref name="prefix"/>: its path under the folder and its resource name.</summary>
    internal static IEnumerable<(string RelativePath, string ResourceName)> Resources(string prefix) =>
        typeof(BuiltInSkillFiles).Assembly.GetManifestResourceNames()
            // RecursiveDir puts backslashes in the resource names when Fleet is built on Windows.
            .Select(name => (Normalized: name.Replace('\\', '/'), Name: name))
            .Where(resource => resource.Normalized.StartsWith(prefix, StringComparison.Ordinal))
            .Select(resource => (Path.Combine(resource.Normalized[prefix.Length..].Split('/')), resource.Name));

    /// <summary>
    /// Writes <paramref name="content"/> unless an identical copy is already there, through a temp file, so a starting
    /// process never reads half a file.
    /// </summary>
    internal static void WriteIfChanged(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>The built-in skills an owner turned on, and the text of their own version of any they made one of.</summary>
internal sealed record OwnerSkills(IReadOnlyList<string> Names, IReadOnlyDictionary<string, string> Yours)
{
    public static OwnerSkills None { get; } = new([], new Dictionary<string, string>(StringComparer.Ordinal));
}
