using WeaveFleet.Domain.Skills;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class HarnessInstallPathsTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "home");
    private static readonly string Repo = Path.Combine(Path.GetTempPath(), "src", "repo");

    [Fact]
    public void Global_UsesDotConfigOpenCodeUnderHome_OnEveryOs()
    {
        var paths = new HarnessInstallPaths(Home);

        paths.SkillDirectory("opencode", InstallTarget.Global, "s").ShouldBe(Path.Combine(Home, ".config", "opencode", "skills", "s"));
        paths.OpenCodeToolsDirectory(InstallTarget.Global).ShouldBe(Path.Combine(Home, ".config", "opencode", "tools"));
        paths.OpenCodeConfigDirectory(InstallTarget.Global).ShouldBe(Path.Combine(Home, ".config", "opencode"));
        paths.SkillDirectory("claude-code", InstallTarget.Global, "s").ShouldBe(Path.Combine(Home, ".claude", "skills", "s"));
    }

    [Fact]
    public void Global_FollowsXdgConfigHome_LikeOpenCode()
    {
        var xdg = Path.Combine(Path.GetTempPath(), "xdg");

        new HarnessInstallPaths(Home, xdg).OpenCodeGlobalDirectory.ShouldBe(Path.Combine(xdg, "opencode"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/config")]
    public void Global_IgnoresAnEmptyOrRelativeXdgConfigHome(string xdg)
    {
        new HarnessInstallPaths(Home, xdg).OpenCodeGlobalDirectory.ShouldBe(Path.Combine(Home, ".config", "opencode"));
    }

    [Fact]
    public void Project_UsesTheRepositorysHarnessFolders()
    {
        var paths = new HarnessInstallPaths(Home);
        var target = InstallTarget.Project(Repo);

        paths.SkillDirectory("opencode", target, "s").ShouldBe(Path.Combine(Repo, ".opencode", "skills", "s"));
        paths.OpenCodeToolsDirectory(target).ShouldBe(Path.Combine(Repo, ".opencode", "tools"));
        paths.OpenCodeConfigDirectory(target).ShouldBe(Repo);
        paths.SkillDirectory("claude-code", target, "s").ShouldBe(Path.Combine(Repo, ".claude", "skills", "s"));
    }

    [Fact]
    public void UnknownHarness_HasNoSkillsFolder()
    {
        new HarnessInstallPaths(Home).SkillDirectory("aider", InstallTarget.Global, "s").ShouldBeNull();
    }

    [Fact]
    public void OpenCode2_UsesItsSeparateConfigFolder_AndTheRepositorysOpenCodeFolder()
    {
        var paths = new HarnessInstallPaths(Home);

        paths.SkillDirectory("opencode2", InstallTarget.Global, "s")
            .ShouldBe(Path.Combine(Home, ".weave", "harnesses", "opencode2", "config", "skills", "s"));
        paths.SkillDirectory("opencode2", InstallTarget.Project(Repo), "s").ShouldBe(Path.Combine(Repo, ".opencode", "skills", "s"));
    }

    [Fact]
    public void A_skill_for_OpenCode_goes_to_OpenCode2_only_once_its_separate_config_folder_exists()
    {
        var home = Directory.CreateTempSubdirectory("fleet-paths-").FullName;
        try
        {
            var paths = new HarnessInstallPaths(home);
            paths.SkillTargets(["opencode", "claude-code"]).ShouldBe(["opencode", "claude-code"]);

            Directory.CreateDirectory(paths.OpenCode2GlobalDirectory);

            paths.SkillTargets(["opencode", "claude-code"]).ShouldBe(["opencode", "claude-code", "opencode2"]);
            paths.SkillTargets(["opencode2", "opencode"]).ShouldBe(["opencode2", "opencode"]);
            paths.SkillTargets(["claude-code"]).ShouldBe(["claude-code"]);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
