using WeaveFleet.Application.Skills;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Workflows;

/// <summary>
/// Whether a skill a step uses is one of Fleet's built-in skills that the user turned off. A skill of the user's own
/// isn't Fleet's to check. Checked when a run starts, and again as each step starts: a run can wait for days.
/// </summary>
public sealed class WorkflowSkills(IBuiltInSkillCatalog catalog, IUserPreferenceRepository preferences)
{
    public async Task<bool> IsOffAsync(string? skill)
    {
        if (skill is null || !catalog.Skills.Any(s => s.Name == skill))
            return false;
        var on = await BuiltInSkillService.GetEnabledAsync(preferences).ConfigureAwait(false);
        return !on.Contains(skill);
    }

    /// <summary>What the step's card says, e.g. "Review uses fleet-code-review, which is now off."</summary>
    public static string NowOffMessage(WorkflowAgentStep step) => $"{step.Title} uses {step.Skill}, which is now off.";
}
