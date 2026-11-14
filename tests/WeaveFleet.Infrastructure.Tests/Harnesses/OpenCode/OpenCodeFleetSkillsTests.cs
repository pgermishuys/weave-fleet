using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeFleetSkillsTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"fleet-skills-{Guid.NewGuid():N}");

    private string SkillsDirectory => Path.Combine(_dataDirectory, "opencode", "skills");

    private string FleetSkill => Path.Combine(SkillsDirectory, "fleet", "SKILL.md");

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
        repoFiles.ShouldContain(Path.Combine("fleet", "SKILL.md"));
        foreach (var file in repoFiles)
            File.ReadAllText(Path.Combine(SkillsDirectory, file)).ShouldBe(File.ReadAllText(Path.Combine(repoSkills, file)));
    }

    [Fact]
    public void Install_leaves_an_identical_copy_alone()
    {
        OpenCodeFleetSkills.Install(_dataDirectory);
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FleetSkill, written);

        OpenCodeFleetSkills.Install(_dataDirectory);

        File.GetLastWriteTimeUtc(FleetSkill).ShouldBe(written);
    }

    [Fact]
    public void Install_replaces_a_copy_that_differs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FleetSkill)!);
        File.WriteAllText(FleetSkill, "an older skill");

        OpenCodeFleetSkills.Install(_dataDirectory);

        File.ReadAllText(FleetSkill).ShouldBe(File.ReadAllText(Path.Combine(RepoSkillsDirectory(), "fleet", "SKILL.md")));
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
        File.Exists(FleetSkill).ShouldBeTrue();
    }

    [Fact]
    public void Install_removes_the_folder_of_the_skill_Fleet_renamed()
    {
        var old = Path.Combine(SkillsDirectory, "fleet-api");
        Directory.CreateDirectory(Path.Combine(old, "scripts"));
        File.WriteAllText(Path.Combine(old, "SKILL.md"), "from an older Fleet");
        File.WriteAllText(Path.Combine(old, "scripts", "fleet-api.sh"), "from an older Fleet");

        OpenCodeFleetSkills.Install(_dataDirectory);

        Directory.Exists(old).ShouldBeFalse();
        File.Exists(FleetSkill).ShouldBeTrue();
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
