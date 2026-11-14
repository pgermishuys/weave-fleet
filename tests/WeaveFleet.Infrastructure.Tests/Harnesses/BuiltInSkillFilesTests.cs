using WeaveFleet.Application.Skills;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

/// <summary>The built-in skills Fleet ships, as Settings and every harness see them.</summary>
public sealed class BuiltInSkillFilesTests
{
    [Fact]
    public void BuiltIn_lists_each_skill_folder_by_the_name_in_its_front_matter()
    {
        var folders = Directory.GetDirectories(RepoSkillsDirectory("built-in-skills"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        folders.ShouldNotBeEmpty();
        BuiltInSkillFiles.BuiltIn.Select(skill => skill.Name).ShouldBe(folders);
    }

    [Fact]
    public void Built_in_skills_have_names_that_cant_take_the_place_of_the_users_own()
    {
        // OpenCode loads skills in parallel and the last one with a name wins, so a user's "code-review" and Fleet's
        // would each win some of the time. Fleet's names start with "fleet-".
        foreach (var skill in BuiltInSkillFiles.BuiltIn)
            skill.Name.ShouldMatch("^fleet-[a-z0-9]+(-[a-z0-9]+)*$");
    }

    [Fact]
    public void Built_in_skill_descriptions_are_plain_one_line_values()
    {
        // A ": " or " #" in an unquoted YAML value breaks the front matter, and OpenCode then skips the skill.
        foreach (var skill in BuiltInSkillFiles.BuiltIn)
        {
            skill.Description.ShouldNotContain(": ");
            skill.Description.ShouldNotContain(" #");
            skill.Description.Length.ShouldBeLessThanOrEqualTo(1024);
        }
    }

    [Fact]
    public void ParseFrontMatter_needs_a_name_and_a_description()
    {
        SkillVersions.ParseFrontMatter("# No front matter").ShouldBeNull();
        SkillVersions.ParseFrontMatter("---\nname: fleet-x\n---\n").ShouldBeNull();
        SkillVersions.ParseFrontMatter("---\r\nname: fleet-x\r\ndescription: Does x.\r\n---\r\n# X")
            .ShouldBe(new BuiltInSkill("fleet-x", "Does x."));
    }

    private static string RepoSkillsDirectory(
        string folder = "skills",
        [System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(testFile)!);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "opencode", folder)))
            directory = directory.Parent;

        return Path.Combine(directory!.FullName, "opencode", folder);
    }
}
