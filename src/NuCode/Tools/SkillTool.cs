using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace NuCode.Tools;

/// <summary>
/// Loads a skill (SKILL.md file) and returns its content in the conversation.
/// Skills are discovered from configuration and the .nucode/skills/ directory.
/// </summary>
internal sealed class SkillTool(ISkillProvider skillProvider) : INuCodeTool
{
    public string Name => "skill";
    public string Description => "Load a skill by name and return its content. Skills provide specialized instructions for particular tasks.";

    public AIFunction ToAIFunction() =>
        AIFunctionFactory.Create(ExecuteAsync, new AIFunctionFactoryOptions
        {
            Name = Name,
            Description = Description,
        });

    [Description("Load a skill by name and return its content. Skills provide specialized instructions for particular tasks.")]
    internal async Task<string> ExecuteAsync(
        [Description("The name of the skill to load")] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Error: skill name is required.";
        }

        var content = await skillProvider.GetSkillContentAsync(name.Trim(), cancellationToken);
        if (content is null)
        {
            var available = skillProvider.GetAvailableSkills();
            var suggestion = available.Count > 0
                ? $" Available skills: {string.Join(", ", available)}"
                : " No skills are currently configured.";
            return $"Error: Skill '{name}' not found.{suggestion}";
        }

        return content;
    }
}
