using System.Security.Cryptography;
using System.Text;

namespace WeaveFleet.Application.Skills;

/// <summary>One of the user's versions of a built-in skill. Its text never changes once saved.</summary>
/// <param name="Number">1 for the first version, counting up.</param>
/// <param name="Note">Why it changed, in the user's words; null when they didn't say.</param>
/// <param name="SessionId">The session it came from, when it was made with Improve.</param>
/// <param name="FleetHash">Fleet's version of the skill when this one was saved (<see cref="SkillVersions.Hash"/>).</param>
public sealed record SkillVersion(
    int Number,
    DateTimeOffset CreatedAt,
    string? Note,
    string? SessionId,
    string? SessionTitle,
    string FleetHash);

/// <summary>Where a new version came from: the note, and the session when it was made with Improve.</summary>
public sealed record SkillVersionSource(string? Note, string? SessionId, string? SessionTitle);

/// <summary>
/// A user's versions of one built-in skill, and which one sessions get.
/// </summary>
/// <param name="Active">The version sessions get; null for Fleet's.</param>
/// <param name="KeptFleetHash">
/// Fleet's version the user last said to keep theirs over. Fleet changed its version when its hash differs from this,
/// or, before the user said anything, from the one the active version was based on.
/// </param>
public sealed record SkillVersionHistory(string Name, int? Active, string? KeptFleetHash, IReadOnlyList<SkillVersion> Versions)
{
    public static SkillVersionHistory Empty(string name) => new(name, null, null, []);

    public SkillVersion? ActiveVersion => Active is { } number ? Versions.FirstOrDefault(v => v.Number == number) : null;

    /// <summary>The Fleet version the active one is measured against, or null when sessions get Fleet's.</summary>
    public string? FleetBaseline => ActiveVersion is { } active ? KeptFleetHash ?? active.FleetHash : null;
}

/// <summary>
/// Keeps each user's versions of Fleet's built-in skills, which Fleet never overwrites. A harness gets the active
/// version's folder in place of Fleet's copy, so two skills with the same name never reach it.
/// </summary>
public interface ISkillVersionStore
{
    Task<SkillVersionHistory> GetAsync(string userId, string name, CancellationToken ct = default);

    /// <summary>Every skill the user has versions of.</summary>
    Task<IReadOnlyList<SkillVersionHistory>> ListAsync(string userId, CancellationToken ct = default);

    /// <summary>Saves <paramref name="content"/> as the next version and makes it the active one.</summary>
    /// <param name="fleetContent">Fleet's version at the time, kept so a later change to it can be shown.</param>
    Task<SkillVersion> AddAsync(
        string userId, string name, string content, SkillVersionSource source, string fleetContent, CancellationToken ct = default);

    /// <summary>Makes <paramref name="number"/> the version sessions get, or Fleet's when null.</summary>
    Task SetActiveAsync(string userId, string name, int? number, CancellationToken ct = default);

    /// <summary>The user keeps their version over <paramref name="fleetContent"/>, Fleet's current one.</summary>
    Task KeepOverAsync(string userId, string name, string fleetContent, CancellationToken ct = default);

    /// <summary>A version's text, or null when there's no such version.</summary>
    Task<string?> ReadAsync(string userId, string name, int number, CancellationToken ct = default);

    /// <summary>Fleet's version with this hash, as it was kept when a version was saved; null when it wasn't kept.</summary>
    Task<string?> ReadFleetAsync(string userId, string name, string fleetHash, CancellationToken ct = default);

    /// <summary>The folder a harness loads a version from: it holds <c>{name}/SKILL.md</c>.</summary>
    string FolderFor(string userId, string name, int number);
}

/// <summary>Helpers for skill text: its hash, and the check a new version has to pass.</summary>
public static class SkillVersions
{
    /// <summary>The largest SKILL.md Fleet saves as a version.</summary>
    public const int MaxLength = 100_000;

    /// <summary>A short hash of a skill's text, for telling Fleet's versions apart.</summary>
    public static string Hash(string content)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(content))))[..16];

    /// <summary>Line endings as <c>\n</c>, so a copy saved on Windows hashes like the original.</summary>
    public static string Normalize(string content) => content.ReplaceLineEndings("\n");

    /// <summary>
    /// The <c>name</c> and <c>description</c> lines of a skill's front matter, or null when either is missing. Only
    /// single-line values, which is how Fleet's skills are written.
    /// </summary>
    public static BuiltInSkill? ParseFrontMatter(string content)
    {
        var lines = Normalize(content).Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
            return null;

        string? name = null, description = null;
        foreach (var line in lines.Skip(1).TakeWhile(line => line.TrimEnd() != "---"))
        {
            if (line.StartsWith("name:", StringComparison.Ordinal))
                name = line["name:".Length..].Trim();
            else if (line.StartsWith("description:", StringComparison.Ordinal))
                description = line["description:".Length..].Trim();
        }

        return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(description) ? null : new BuiltInSkill(name, description);
    }

    /// <summary>Why <paramref name="content"/> can't be a version of <paramref name="name"/>; null when it can.</summary>
    public static string? Problem(string name, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "The skill is empty.";
        if (content.Length > MaxLength)
            return $"Keep the skill under {MaxLength:N0} characters.";

        var front = ParseFrontMatter(content);
        if (front is null)
            return $"The skill has to start with front matter: a --- line, then name: {name} and a one-line description:, then another --- line.";
        if (front.Name != name)
            return $"Keep the name in the front matter as {name}: it's how sessions find the skill.";
        return null;
    }
}
