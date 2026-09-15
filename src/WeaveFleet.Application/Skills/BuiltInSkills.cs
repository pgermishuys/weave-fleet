using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Skills;

/// <summary>A skill that ships with Fleet, which the user can turn on for their sessions.</summary>
public sealed record BuiltInSkill(string Name, string Description);

/// <summary>The skills Fleet ships for users to turn on, in name order.</summary>
public interface IBuiltInSkillCatalog
{
    IReadOnlyList<BuiltInSkill> Skills { get; }
}

/// <summary>A built-in skill as the API shows it, with whether the user turned it on.</summary>
public sealed record BuiltInSkillView(string Name, string Description, bool Enabled);

/// <summary>
/// Which of Fleet's built-in skills the user turned on. Each is off until the user turns it on, so a skill of their own
/// never has company they didn't ask for. The choice is one user preference: the names, comma-separated.
/// </summary>
public sealed class BuiltInSkillService(IBuiltInSkillCatalog catalog, IUserPreferenceRepository preferences)
{
    public const string PreferenceKey = "BuiltInSkills";

    public async Task<IReadOnlyList<BuiltInSkillView>> ListAsync()
    {
        var enabled = await GetEnabledAsync(preferences).ConfigureAwait(false);
        return catalog.Skills
            .Select(skill => new BuiltInSkillView(skill.Name, skill.Description, enabled.Contains(skill.Name)))
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
        return new BuiltInSkillView(skill.Name, skill.Description, enabled);
    }

    /// <summary>The names the user turned on. It can include skills this Fleet no longer ships.</summary>
    public static async Task<IReadOnlySet<string>> GetEnabledAsync(IUserPreferenceRepository preferences)
    {
        var value = await preferences.GetAsync(PreferenceKey).ConfigureAwait(false);
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}
