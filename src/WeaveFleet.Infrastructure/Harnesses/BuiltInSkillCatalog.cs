using WeaveFleet.Application.Skills;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>The built-in skills Fleet embeds, for Settings.</summary>
internal sealed class BuiltInSkillCatalog : IBuiltInSkillCatalog
{
    public IReadOnlyList<BuiltInSkill> Skills => BuiltInSkillFiles.BuiltIn;

    public string? ContentOf(string name) => BuiltInSkillFiles.ContentOf(name);
}
