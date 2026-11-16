using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>Which new sessions have the workflow step tool denied, when a busy server still has an older setup.</summary>
public sealed class OpenCode2StepToolRulesTests
{
    [Theory]
    // A server without the tool has nothing to deny.
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    // Workflows on, the server has the tool: only a step keeps it.
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    // Workflows turned off while the server kept its older setup: no session keeps the tool, a step included.
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    public void A_new_session_has_the_step_tool_denied_unless_it_is_a_step_and_workflows_are_on(
        bool serverHasStepTool, bool workflowsOnNow, bool workflowStep, bool denied)
        => OpenCode2HarnessRuntime.HidesStepTool(serverHasStepTool, workflowsOnNow, workflowStep).ShouldBe(denied);
}
