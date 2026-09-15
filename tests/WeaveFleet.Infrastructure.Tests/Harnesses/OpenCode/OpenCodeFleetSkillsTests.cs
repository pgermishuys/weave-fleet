using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeFleetSkillsTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"fleet-skills-{Guid.NewGuid():N}");

    private string SkillsDirectory => Path.Combine(_dataDirectory, "opencode", "skills");

    private string FleetApiSkill => Path.Combine(SkillsDirectory, "fleet-api", "SKILL.md");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
            Directory.Delete(_dataDirectory, recursive: true);
    }

    [Fact]
    public void Install_writes_every_skill_in_the_repo_and_returns_their_folder()
    {
        var installed = OpenCodeFleetSkills.Install(_dataDirectory);

        installed.ShouldBe(SkillsDirectory);
        var repoSkills = RepoSkillsDirectory();
        var repoFiles = Directory.GetFiles(repoSkills, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(repoSkills, file))
            .Order(StringComparer.Ordinal)
            .ToList();
        Directory.GetFiles(SkillsDirectory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(SkillsDirectory, file))
            .Order(StringComparer.Ordinal)
            .ShouldBe(repoFiles);
        repoFiles.ShouldContain(Path.Combine("fleet-api", "SKILL.md"));
        foreach (var file in repoFiles)
            File.ReadAllText(Path.Combine(SkillsDirectory, file)).ShouldBe(File.ReadAllText(Path.Combine(repoSkills, file)));
    }

    [Fact]
    public void Install_leaves_an_identical_copy_alone()
    {
        OpenCodeFleetSkills.Install(_dataDirectory);
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FleetApiSkill, written);

        OpenCodeFleetSkills.Install(_dataDirectory);

        File.GetLastWriteTimeUtc(FleetApiSkill).ShouldBe(written);
    }

    [Fact]
    public void Install_replaces_a_copy_that_differs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FleetApiSkill)!);
        File.WriteAllText(FleetApiSkill, "an older skill");

        OpenCodeFleetSkills.Install(_dataDirectory);

        File.ReadAllText(FleetApiSkill).ShouldBe(File.ReadAllText(Path.Combine(RepoSkillsDirectory(), "fleet-api", "SKILL.md")));
    }

    [Fact]
    public void Install_deletes_files_Fleet_no_longer_ships()
    {
        var oldScript = Path.Combine(SkillsDirectory, "fleet-api", "scripts", "fleet-api.sh");
        var oldSkill = Path.Combine(SkillsDirectory, "retired", "SKILL.md");
        foreach (var file in new[] { oldScript, oldSkill })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "from an older Fleet");
        }

        OpenCodeFleetSkills.Install(_dataDirectory);

        File.Exists(oldScript).ShouldBeFalse();
        File.Exists(oldSkill).ShouldBeFalse();
        File.Exists(FleetApiSkill).ShouldBeTrue();
    }

    [Fact]
    public void InstallBuiltIn_writes_every_built_in_skill_in_the_repo_and_returns_their_folder()
    {
        var builtInDirectory = Path.Combine(_dataDirectory, "opencode", "built-in-skills");

        var installed = OpenCodeFleetSkills.InstallBuiltIn(_dataDirectory);

        installed.ShouldBe(builtInDirectory);
        var repoSkills = RepoSkillsDirectory("built-in-skills");
        Directory.GetFiles(builtInDirectory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(builtInDirectory, file))
            .Order(StringComparer.Ordinal)
            .ShouldBe(Directory.GetFiles(repoSkills, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(repoSkills, file))
                .Order(StringComparer.Ordinal));
        Directory.Exists(Path.Combine(SkillsDirectory, "fleet-simplify")).ShouldBeFalse();
    }

    [Fact]
    public void BuiltIn_lists_each_skill_folder_by_the_name_in_its_front_matter()
    {
        var folders = Directory.GetDirectories(RepoSkillsDirectory("built-in-skills"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        folders.ShouldNotBeEmpty();
        OpenCodeFleetSkills.BuiltIn.Select(skill => skill.Name).ShouldBe(folders);
    }

    [Fact]
    public void Built_in_skills_have_names_that_cant_take_the_place_of_the_users_own()
    {
        // OpenCode loads skills in parallel and the last one with a name wins, so a user's "code-review" and Fleet's
        // would each win some of the time. Fleet's names start with "fleet-".
        foreach (var skill in OpenCodeFleetSkills.BuiltIn)
            skill.Name.ShouldMatch("^fleet-[a-z0-9]+(-[a-z0-9]+)*$");
    }

    [Fact]
    public void Built_in_skill_descriptions_are_plain_one_line_values()
    {
        // A ": " or " #" in an unquoted YAML value breaks the front matter, and OpenCode then skips the skill.
        foreach (var skill in OpenCodeFleetSkills.BuiltIn)
        {
            skill.Description.ShouldNotContain(": ");
            skill.Description.ShouldNotContain(" #");
            skill.Description.Length.ShouldBeLessThanOrEqualTo(1024);
        }
    }

    [Fact]
    public void ParseFrontMatter_needs_a_name_and_a_description()
    {
        OpenCodeFleetSkills.ParseFrontMatter("# No front matter").ShouldBeNull();
        OpenCodeFleetSkills.ParseFrontMatter("---\nname: fleet-x\n---\n").ShouldBeNull();
        OpenCodeFleetSkills.ParseFrontMatter("---\r\nname: fleet-x\r\ndescription: Does x.\r\n---\r\n# X")
            .ShouldBe(new WeaveFleet.Application.Skills.BuiltInSkill("fleet-x", "Does x."));
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
