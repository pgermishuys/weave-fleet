using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Copilot;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Copilot;

/// <summary>Fleet's own skills and the built-in skills an owner turned on, in the folder weave-copilot gives Copilot as a skill directory.</summary>
public sealed class CopilotFleetSkillsTests : IDisposable
{
    private readonly string _dataDirectory = Directory.CreateTempSubdirectory("fleet-copilot-skills-").FullName;

    public void Dispose() => Directory.Delete(_dataDirectory, recursive: true);

    [Fact]
    public void The_skill_folders_are_directly_in_the_owners_folder()
    {
        var names = BuiltInSkillFiles.Names.Order(StringComparer.Ordinal).Take(2).ToList();

        var folder = CopilotFleetSkills.Sync(_dataDirectory, "local-user", [names[1], names[0], "not-a-fleet-skill"]);

        folder.ShouldBe(Path.Combine(_dataDirectory, "copilot", "skills", BuiltInSkillFiles.OwnerFolder("local-user")));
        Directory.GetDirectories(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(["fleet", .. names]);
        File.Exists(Path.Combine(folder, names[0], "SKILL.md")).ShouldBeTrue();
    }

    [Fact]
    public void A_skill_turned_off_leaves_but_Fleets_own_skill_stays()
    {
        var name = BuiltInSkillFiles.Names.Order(StringComparer.Ordinal).First();
        var folder = CopilotFleetSkills.Sync(_dataDirectory, "local-user", [name]);

        CopilotFleetSkills.Sync(_dataDirectory, "local-user", []);

        Directory.GetDirectories(folder).Select(Path.GetFileName).ShouldBe(["fleet"]);
    }
}
