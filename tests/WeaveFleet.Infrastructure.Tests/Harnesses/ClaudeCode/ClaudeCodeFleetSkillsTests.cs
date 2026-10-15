using Shouldly;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>Fleet's own skills and the built-in skills an owner turned on, in the folder Claude Code takes with <c>--add-dir</c>.</summary>
public sealed class ClaudeCodeFleetSkillsTests : IDisposable
{
    private readonly string _dataDirectory = Directory.CreateTempSubdirectory("fleet-claude-skills-").FullName;

    public void Dispose() => Directory.Delete(_dataDirectory, recursive: true);

    [Fact]
    public void The_skills_turned_on_are_in_the_folders_claude_skills()
    {
        var names = BuiltInSkillFiles.Names.Order(StringComparer.Ordinal).Take(2).ToList();

        var skills = ClaudeCodeFleetSkills.Sync(_dataDirectory, "local-user", [names[1], names[0], "not-a-fleet-skill"]).ShouldNotBeNull();

        skills.Folder.ShouldBe(Path.Combine(_dataDirectory, "claude-code", "skills", BuiltInSkillFiles.OwnerFolder("local-user")));
        skills.Skills.ShouldBe(string.Join(',', ["fleet", .. names]));
        // Claude Code loads an added folder's .claude/skills under the skills' own names.
        var loaded = Path.Combine(skills.Folder, ".claude", "skills");
        Directory.GetDirectories(loaded).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(["fleet", .. names]);
        File.Exists(Path.Combine(loaded, names[0], "SKILL.md")).ShouldBeTrue();
        Directory.GetFileSystemEntries(skills.Folder).Select(Path.GetFileName).ShouldBe([".claude"]);
    }

    [Fact]
    public void A_skill_the_owner_made_their_own_version_of_gets_their_text()
    {
        var name = BuiltInSkillFiles.Names.Order(StringComparer.Ordinal).First();
        var mine = $"---\nname: {name}\ndescription: Mine\n---\n\nMy way.\n";

        var skills = ClaudeCodeFleetSkills.Sync(_dataDirectory, "local-user", [name], new Dictionary<string, string> { [name] = mine }).ShouldNotBeNull();

        File.ReadAllText(Path.Combine(skills.Folder, ".claude", "skills", name, "SKILL.md")).ShouldBe(mine);
    }

    [Fact]
    public void A_skill_turned_off_leaves_but_Fleets_own_skill_stays()
    {
        var names = BuiltInSkillFiles.Names.Order(StringComparer.Ordinal).Take(2).ToList();
        var skills = ClaudeCodeFleetSkills.Sync(_dataDirectory, "local-user", names).ShouldNotBeNull();

        ClaudeCodeFleetSkills.Sync(_dataDirectory, "local-user", [names[0]]).ShouldNotBeNull().Skills.ShouldBe($"fleet,{names[0]}");
        Directory.Exists(Path.Combine(skills.Folder, ".claude", "skills", names[1])).ShouldBeFalse();

        ClaudeCodeFleetSkills.Sync(_dataDirectory, "local-user", []).ShouldNotBeNull().Skills.ShouldBe("fleet");
        File.Exists(Path.Combine(skills.Folder, ".claude", "skills", "fleet", "SKILL.md")).ShouldBeTrue();
        Directory.Exists(Path.Combine(skills.Folder, ".claude", "skills", names[0])).ShouldBeFalse();
    }

    [Fact]
    public void With_no_built_in_skill_on_the_fleet_skill_and_its_reference_files_are_still_there()
    {
        var skills = ClaudeCodeFleetSkills.Sync(_dataDirectory, "local-user", []).ShouldNotBeNull();

        var fleet = Path.Combine(skills.Folder, ".claude", "skills", "fleet");
        foreach (var file in new[] { "SKILL.md", "sessions.md", "automations.md", "workflows.md", "memory.md", "settings.md" })
            File.Exists(Path.Combine(fleet, file)).ShouldBeTrue(file);
    }

    [Fact]
    public void Each_owner_has_a_folder_of_their_own()
    {
        var name = BuiltInSkillFiles.Names.Order(StringComparer.Ordinal).First();

        var mine = ClaudeCodeFleetSkills.Sync(_dataDirectory, "owner-1", [name]).ShouldNotBeNull();
        var theirs = ClaudeCodeFleetSkills.Sync(_dataDirectory, "owner-2", [name]).ShouldNotBeNull();

        mine.Folder.ShouldNotBe(theirs.Folder);
    }
}
