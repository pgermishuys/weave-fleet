using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Services;

public sealed class KeyFileConfigTests
{
    [Fact]
    public void load_returns_config_without_throwing()
    {
        var config = KeyFileConfig.Load();
        config.ShouldNotBeNull();
    }

    [Fact]
    public void load_returns_non_empty_groups()
    {
        var config = KeyFileConfig.Load();
        config.Groups.ShouldNotBeEmpty();
    }

    [Fact]
    public void load_contains_dotnet_solution_group()
    {
        var config = KeyFileConfig.Load();
        var group = config.Groups.FirstOrDefault(g => g.Id == "dotnet-solution");

        group.ShouldNotBeNull();
        group.Extensions.ShouldNotBeNull();
        group.Extensions.ShouldContain(".sln");
        group.Extensions.ShouldContain(".slnx");
        group.CompatibleTools.ShouldContain("rider");
        group.CompatibleTools.ShouldContain("visual-studio");
    }

    [Fact]
    public void load_dotnet_solution_trumps_dotnet_project()
    {
        var config = KeyFileConfig.Load();
        var solution = config.Groups.First(g => g.Id == "dotnet-solution");

        solution.Trumps.ShouldNotBeNull();
        solution.Trumps.ShouldContain("dotnet-project");
    }

    [Fact]
    public void load_is_idempotent_returns_same_instance()
    {
        var first = KeyFileConfig.Load();
        var second = KeyFileConfig.Load();

        first.ShouldBeSameAs(second);
    }
}
