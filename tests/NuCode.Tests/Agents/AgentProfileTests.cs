using System.Collections.Immutable;
using NuCode.Agents;

namespace NuCode;

public sealed class AgentProfileTests
{
    [Fact]
    public void CanCreateProfileWithRequiredPropertiesOnly()
    {
        var profile = new AgentProfile { Name = "test" };

        profile.Name.ShouldBe("test");
        profile.Mode.ShouldBe(AgentMode.Primary);
        profile.Description.ShouldBeNull();
        profile.SystemPrompt.ShouldBeNull();
        profile.Temperature.ShouldBeNull();
        profile.TopP.ShouldBeNull();
        profile.MaxSteps.ShouldBeNull();
        profile.AllowedTools.ShouldBeNull();
        profile.DeniedTools.ShouldBeNull();
        profile.PermissionRulesetName.ShouldBeNull();
        profile.IsHidden.ShouldBeFalse();
        profile.IsNative.ShouldBeFalse();
        profile.Options.ShouldBeEmpty();
    }

    [Fact]
    public void WithExpressionCreatesModifiedCopy()
    {
        var original = new AgentProfile
        {
            Name = "build",
            Mode = AgentMode.Primary,
            Temperature = 0.7,
        };

        var modified = original with { Temperature = 0.3, IsHidden = true };

        modified.Name.ShouldBe("build");
        modified.Temperature.ShouldBe(0.3);
        modified.IsHidden.ShouldBeTrue();
        // Original is unchanged
        original.Temperature.ShouldBe(0.7);
        original.IsHidden.ShouldBeFalse();
    }

    [Fact]
    public void ToolFilterSetsAreImmutable()
    {
        var allowed = ImmutableHashSet.Create("read", "glob", "grep");
        var denied = ImmutableHashSet.Create("bash", "write");

        var profile = new AgentProfile
        {
            Name = "readonly",
            AllowedTools = allowed,
            DeniedTools = denied,
        };

        profile.AllowedTools!.Count.ShouldBe(3);
        profile.DeniedTools!.Count.ShouldBe(2);
        profile.AllowedTools.ShouldContain("read");
        profile.DeniedTools.ShouldContain("bash");
    }

    [Fact]
    public void OptionsDefaultsToEmptyDictionary()
    {
        var profile = new AgentProfile { Name = "test" };

        profile.Options.ShouldNotBeNull();
        profile.Options.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(AgentMode.Primary)]
    [InlineData(AgentMode.SubAgent)]
    [InlineData(AgentMode.All)]
    public void AllAgentModesAreAssignable(AgentMode mode)
    {
        var profile = new AgentProfile { Name = "test", Mode = mode };

        profile.Mode.ShouldBe(mode);
    }
}
