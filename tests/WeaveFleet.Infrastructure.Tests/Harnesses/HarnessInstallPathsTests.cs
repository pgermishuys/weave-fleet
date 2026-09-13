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
}
