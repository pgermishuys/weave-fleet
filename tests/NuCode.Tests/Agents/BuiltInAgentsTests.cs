using NuCode.Agents;

namespace NuCode;

public sealed class BuiltInAgentsTests
{
    [Fact]
    public void GetAllReturnsSevenBuiltInAgents()
    {
        var agents = BuiltInAgents.GetAll();

        agents.Length.ShouldBe(7);
    }

    [Fact]
    public void AllBuiltInAgentsAreNative()
    {
        var agents = BuiltInAgents.GetAll();

        agents.ShouldAllBe(a => a.IsNative);
    }

    [Fact]
    public void AllBuiltInAgentsHaveUniqueNames()
    {
        var agents = BuiltInAgents.GetAll();
        var names = agents.Select(a => a.Name).ToList();

        names.Distinct().Count().ShouldBe(names.Count);
    }

    [Fact]
    public void BuildIsDefaultPrimaryAgent()
    {
        var build = BuiltInAgents.Build();

        build.Name.ShouldBe("build");
        build.Mode.ShouldBe(AgentMode.Primary);
        build.IsHidden.ShouldBeFalse();
        build.AllowedTools.ShouldBeNull();
        build.DeniedTools.ShouldBeNull();
    }

    [Fact]
    public void PlanDisallowsEditTools()
    {
        var plan = BuiltInAgents.Plan();

        plan.Name.ShouldBe("plan");
        plan.Mode.ShouldBe(AgentMode.Primary);
        plan.IsHidden.ShouldBeFalse();
        plan.PermissionRulesetName.ShouldBe("plan");
    }

    [Fact]
    public void GeneralIsSubAgentWithDeniedTodoTools()
    {
        var general = BuiltInAgents.General();

        general.Name.ShouldBe("general");
        general.Mode.ShouldBe(AgentMode.SubAgent);
        general.IsHidden.ShouldBeFalse();
        general.DeniedTools.ShouldNotBeNull();
        general.DeniedTools.ShouldContain("todoread");
        general.DeniedTools.ShouldContain("todowrite");
    }

    [Fact]
    public void ExploreIsReadOnlySubAgent()
    {
        var explore = BuiltInAgents.Explore();

        explore.Name.ShouldBe("explore");
        explore.Mode.ShouldBe(AgentMode.SubAgent);
        explore.IsHidden.ShouldBeFalse();
        explore.SystemPrompt.ShouldNotBeNull();
        explore.AllowedTools.ShouldNotBeNull();
        explore.AllowedTools.ShouldContain("grep");
        explore.AllowedTools.ShouldContain("glob");
        explore.AllowedTools.ShouldContain("read");
        explore.AllowedTools.ShouldNotContain("write");
        explore.AllowedTools.ShouldNotContain("edit");
    }

    [Fact]
    public void CompactionIsHiddenUtilityAgent()
    {
        var compaction = BuiltInAgents.Compaction();

        compaction.Name.ShouldBe("compaction");
        compaction.IsHidden.ShouldBeTrue();
        compaction.IsNative.ShouldBeTrue();
        compaction.SystemPrompt.ShouldNotBeNull();
    }

    [Fact]
    public void TitleIsHiddenWithCustomTemperature()
    {
        var title = BuiltInAgents.Title();

        title.Name.ShouldBe("title");
        title.IsHidden.ShouldBeTrue();
        title.Temperature.ShouldBe(0.5);
        title.SystemPrompt.ShouldNotBeNull();
    }

    [Fact]
    public void SummaryIsHiddenUtilityAgent()
    {
        var summary = BuiltInAgents.Summary();

        summary.Name.ShouldBe("summary");
        summary.IsHidden.ShouldBeTrue();
        summary.SystemPrompt.ShouldNotBeNull();
    }

    [Fact]
    public void HiddenAgentsAreCompactionTitleAndSummary()
    {
        var agents = BuiltInAgents.GetAll();
        var hidden = agents.Where(a => a.IsHidden).Select(a => a.Name).OrderBy(n => n).ToList();

        hidden.ShouldBe(["compaction", "summary", "title"]);
    }

    [Fact]
    public void SubAgentsAreGeneralAndExplore()
    {
        var agents = BuiltInAgents.GetAll();
        var subAgents = agents.Where(a => a.Mode == AgentMode.SubAgent).Select(a => a.Name).OrderBy(n => n).ToList();

        subAgents.ShouldBe(["explore", "general"]);
    }
}
