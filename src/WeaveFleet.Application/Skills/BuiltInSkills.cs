using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Skills;

/// <summary>A skill that ships with Fleet, which the user can turn on for their sessions.</summary>
public sealed record BuiltInSkill(string Name, string Description);

/// <summary>The skills Fleet ships for users to turn on, in name order.</summary>
public interface IBuiltInSkillCatalog
{
    IReadOnlyList<BuiltInSkill> Skills { get; }

    /// <summary>The skill's <c>SKILL.md</c> as Fleet ships it, or null when Fleet doesn't ship it.</summary>
    string? ContentOf(string name) => null;
}

/// <summary>A built-in skill as the API shows it, with whether the user turned it on.</summary>
/// <param name="Version">The user's version sessions get; null for Fleet's.</param>
/// <param name="FleetChanged">Fleet changed its version since the user's was made, or since they last kept theirs.</param>
/// <param name="VersionCount">How many versions the user made, including ones not in use.</param>
public sealed record BuiltInSkillView(
    string Name, string Description, bool Enabled, int? Version = null, bool FleetChanged = false, int VersionCount = 0);

/// <summary>One of the user's versions, as the history lists it.</summary>
public sealed record SkillVersionView(
    int Number,
    DateTimeOffset CreatedAt,
    string? Note,
    string? SessionId,
    string? SessionTitle,
    bool Active);

/// <summary>One version's text.</summary>
public sealed record SkillVersionContent(string Name, int Version, string Content);

/// <summary>A built-in skill with its text: Fleet's, the user's active version, and their history.</summary>
/// <param name="FleetContent">Fleet's version as this Fleet ships it.</param>
/// <param name="YourContent">The active version's text; null when sessions get Fleet's.</param>
/// <param name="FleetBefore">
/// When <paramref name="FleetChanged"/>, Fleet's version as it was when the user's was made, to show what changed.
/// </param>
public sealed record BuiltInSkillDetail(
    string Name,
    string Description,
    bool Enabled,
    string FleetContent,
    string? YourContent,
    int? Version,
    bool FleetChanged,
    string? FleetBefore,
    IReadOnlyList<SkillVersionView> Versions);

/// <summary>
/// Which of Fleet's built-in skills the user turned on, and the versions of them they made. Each skill is off until the
/// user turns it on, so a skill of their own never has company they didn't ask for. The choice is one user preference:
/// the names, comma-separated. A version of a skill replaces Fleet's copy in the sessions they start afterwards; it's
/// kept apart from Fleet's, so an update never overwrites it. Each harness hears about a change
/// (<see cref="IHarnessRuntime.BuiltInSkillsChangedAsync"/>), so the sessions started afterwards get it.
/// </summary>
public sealed partial class BuiltInSkillService(
    IBuiltInSkillCatalog catalog,
    IUserPreferenceRepository preferences,
    IHarnessRegistry? harnesses = null,
    IUserContext? user = null,
    ILogger<BuiltInSkillService>? logger = null,
    ISkillVersionStore? versions = null,
    ISessionRepository? sessions = null)
{
    public const string PreferenceKey = "BuiltInSkills";

    private const int MaxNoteLength = 2000;

    private string UserId => user?.UserId ?? string.Empty;

    public async Task<IReadOnlyList<BuiltInSkillView>> ListAsync()
    {
        var enabled = await GetEnabledAsync(preferences).ConfigureAwait(false);
        var histories = versions is null
            ? new Dictionary<string, SkillVersionHistory>()
            : (await versions.ListAsync(UserId).ConfigureAwait(false)).ToDictionary(h => h.Name, StringComparer.Ordinal);

        return catalog.Skills
            .Select(skill => ToView(skill, enabled.Contains(skill.Name), histories.GetValueOrDefault(skill.Name)))
            .ToList();
    }

    public async Task<Result<BuiltInSkillView>> SetEnabledAsync(string name, bool enabled)
    {
        var skill = catalog.Skills.FirstOrDefault(skill => skill.Name == name);
        if (skill is null)
            return FleetError.NotFoundFor("BuiltInSkill", name);

        var names = new SortedSet<string>(await GetEnabledAsync(preferences).ConfigureAwait(false), StringComparer.Ordinal);
        if (enabled)
            names.Add(name);
        else
            names.Remove(name);

        await preferences.SetAsync(PreferenceKey, string.Join(',', names)).ConfigureAwait(false);
        await TellHarnessesAsync().ConfigureAwait(false);
        var history = versions is null ? null : await versions.GetAsync(UserId, name).ConfigureAwait(false);
        return ToView(skill, enabled, history);
    }

    /// <summary>The skill with Fleet's text, the user's active version and their history.</summary>
    public async Task<Result<BuiltInSkillDetail>> GetAsync(string name)
    {
        if (Find(name) is not { } found)
            return FleetError.NotFoundFor("BuiltInSkill", name);

        var (skill, fleetContent) = found;
        var enabled = (await GetEnabledAsync(preferences).ConfigureAwait(false)).Contains(name);
        var history = versions is null ? SkillVersionHistory.Empty(name) : await versions.GetAsync(UserId, name).ConfigureAwait(false);

        string? yours = null;
        if (versions is not null && history.Active is { } active)
            yours = await versions.ReadAsync(UserId, name, active).ConfigureAwait(false);

        var changed = FleetChanged(history, fleetContent);
        string? before = null;
        if (changed && versions is not null && history.FleetBaseline is { } baseline)
            before = await versions.ReadFleetAsync(UserId, name, baseline).ConfigureAwait(false);

        return new BuiltInSkillDetail(
            name,
            skill.Description,
            enabled,
            fleetContent,
            yours,
            yours is null ? null : history.Active,
            changed,
            before,
            history.Versions
                .OrderByDescending(v => v.Number)
                .Select(v => new SkillVersionView(v.Number, v.CreatedAt, v.Note, v.SessionId, v.SessionTitle, v.Number == history.Active))
                .ToList());
    }

    /// <summary>
    /// Saves <paramref name="content"/> as the user's next version of the skill, which the sessions they start
    /// afterwards get in place of Fleet's.
    /// </summary>
    /// <param name="note">Why it changed, which the history shows.</param>
    /// <param name="sessionId">The session it was improved from, if any.</param>
    public async Task<Result<BuiltInSkillDetail>> SaveVersionAsync(string name, string? content, string? note, string? sessionId)
    {
        if (versions is null || Find(name) is not { } found)
            return FleetError.NotFoundFor("BuiltInSkill", name);
        if (SkillVersions.Problem(name, content) is { } problem)
            return FleetError.ValidationError("Content", problem);
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note?.Length > MaxNoteLength)
            return FleetError.ValidationError("Note", $"Keep the note under {MaxNoteLength} characters.");

        var history = await versions.GetAsync(UserId, name).ConfigureAwait(false);
        var current = history.Active is { } active
            ? await versions.ReadAsync(UserId, name, active).ConfigureAwait(false) ?? found.Content
            : found.Content;
        if (SkillVersions.Normalize(content!) == SkillVersions.Normalize(current))
            return FleetError.ValidationError("Content", "Nothing changed: this is the text sessions get already.");

        string? title = null;
        if (!string.IsNullOrWhiteSpace(sessionId) && sessions is not null)
            title = (await sessions.GetByIdAsync(sessionId).ConfigureAwait(false))?.Title;

        await versions.AddAsync(UserId, name, content!, new SkillVersionSource(note, sessionId, title), found.Content).ConfigureAwait(false);
        await TellHarnessesAsync().ConfigureAwait(false);
        return await GetAsync(name).ConfigureAwait(false);
    }

    /// <summary>Makes one of the user's versions the one sessions get, or Fleet's when <paramref name="version"/> is null.</summary>
    public async Task<Result<BuiltInSkillDetail>> UseVersionAsync(string name, int? version)
    {
        if (versions is null || Find(name) is null)
            return FleetError.NotFoundFor("BuiltInSkill", name);

        var history = await versions.GetAsync(UserId, name).ConfigureAwait(false);
        if (version is { } number && history.Versions.All(v => v.Number != number))
            return FleetError.NotFoundFor("SkillVersion", $"{name} v{number}");

        await versions.SetActiveAsync(UserId, name, version).ConfigureAwait(false);
        await TellHarnessesAsync().ConfigureAwait(false);
        return await GetAsync(name).ConfigureAwait(false);
    }

    /// <summary>The user keeps their version after Fleet changed its own, which puts the notice away.</summary>
    public async Task<Result<BuiltInSkillDetail>> KeepMineAsync(string name)
    {
        if (versions is null || Find(name) is not { } found)
            return FleetError.NotFoundFor("BuiltInSkill", name);

        await versions.KeepOverAsync(UserId, name, found.Content).ConfigureAwait(false);
        return await GetAsync(name).ConfigureAwait(false);
    }

    /// <summary>One version's text, for comparing it.</summary>
    public async Task<Result<SkillVersionContent>> ReadVersionAsync(string name, int version)
    {
        if (versions is null || Find(name) is null)
            return FleetError.NotFoundFor("BuiltInSkill", name);
        return await versions.ReadAsync(UserId, name, version).ConfigureAwait(false) is { } content
            ? new SkillVersionContent(name, version, content)
            : FleetError.NotFoundFor("SkillVersion", $"{name} v{version}");
    }

    /// <summary>The text sessions get for <paramref name="name"/> now: the user's active version, or Fleet's.</summary>
    internal async Task<(string Content, int? Version)?> CurrentAsync(string name)
    {
        if (Find(name) is not { } found)
            return null;
        if (versions is null)
            return (found.Content, null);

        var history = await versions.GetAsync(UserId, name).ConfigureAwait(false);
        if (history.Active is { } active && await versions.ReadAsync(UserId, name, active).ConfigureAwait(false) is { } yours)
            return (yours, active);
        return (found.Content, null);
    }

    private (BuiltInSkill Skill, string Content)? Find(string name)
        => catalog.Skills.FirstOrDefault(skill => skill.Name == name) is { } skill && catalog.ContentOf(name) is { } content
            ? (skill, content)
            : null;

    private BuiltInSkillView ToView(BuiltInSkill skill, bool enabled, SkillVersionHistory? history)
    {
        var count = history?.Versions.Count ?? 0;
        if (history?.ActiveVersion is null)
            return new BuiltInSkillView(skill.Name, skill.Description, enabled, VersionCount: count);

        var changed = catalog.ContentOf(skill.Name) is { } fleet && FleetChanged(history, fleet);
        return new BuiltInSkillView(skill.Name, skill.Description, enabled, history.Active, changed, count);
    }

    private static bool FleetChanged(SkillVersionHistory history, string fleetContent)
        => history.FleetBaseline is { } baseline && baseline != SkillVersions.Hash(fleetContent);

    private async Task TellHarnessesAsync()
    {
        if (harnesses is null || user is null)
            return;

        foreach (var harness in harnesses.GetAll())
        {
            if (harnesses.GetRuntimeByType(harness.Type) is not { } runtime)
                continue;

            try
            {
                await runtime.BuiltInSkillsChangedAsync(user.UserId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The choice is saved; the harness reads it again when it next starts a session.
                if (logger is not null)
                    LogHarnessFailed(logger, harness.Type, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {HarnessType} harness couldn't take the change to the built-in skills")]
    private static partial void LogHarnessFailed(ILogger logger, string harnessType, Exception exception);

    /// <summary>The names the user turned on. It can include skills this Fleet no longer ships.</summary>
    public static async Task<IReadOnlySet<string>> GetEnabledAsync(IUserPreferenceRepository preferences)
    {
        var value = await preferences.GetAsync(PreferenceKey).ConfigureAwait(false);
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The built-in skills the owner turned on, each with the folder of the version they made when there is one. A
    /// harness loads Fleet's copy for a skill with no folder and the folder for the rest, so only one copy of a name ever
    /// reaches it.
    /// </summary>
    public static async Task<IReadOnlyList<(string Name, int? Version, string? Folder)>> GetSessionSkillsAsync(
        IUserPreferenceRepository preferences, ISkillVersionStore? versions, string ownerUserId, IEnumerable<string> shipped)
    {
        var enabled = await GetEnabledAsync(preferences).ConfigureAwait(false);
        var histories = versions is null
            ? new Dictionary<string, SkillVersionHistory>()
            : (await versions.ListAsync(ownerUserId).ConfigureAwait(false)).ToDictionary(h => h.Name, StringComparer.Ordinal);

        List<(string, int?, string?)> skills = [];
        foreach (var name in shipped.Where(enabled.Contains).Order(StringComparer.Ordinal))
        {
            if (versions is not null && histories.GetValueOrDefault(name)?.Active is { } active)
            {
                var folder = versions.FolderFor(ownerUserId, name, active);
                if (File.Exists(Path.Combine(folder, name, "SKILL.md")))
                {
                    skills.Add((name, active, folder));
                    continue;
                }
            }

            skills.Add((name, null, null));
        }

        return skills;
    }
}
