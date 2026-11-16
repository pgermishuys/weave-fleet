using Shouldly;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>A resumed session gets the step tool denied by the same rule as a new one.</summary>
public sealed class OpenCode2ResumeStepToolTests
{
    [Theory]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, true, false, false)]
    // Workflows turned off while the busy server kept its older setup: a resumed step has the tool denied too.
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, false, true)]
    // Already denied: nothing to add.
    [InlineData(true, false, true, true, false)]
    public void A_resumed_session_gets_the_step_tool_denied_unless_it_is_a_step_and_workflows_are_on(
        bool serverHasStepTool, bool workflowsOnNow, bool workflowStep, bool alreadyDenied, bool add)
    {
        IReadOnlyList<OpenCode2PermissionRule> rules = alreadyDenied ? [.. OpenCode2HttpClient.AllowAll, OpenCode2HttpClient.DenyStepTool] : OpenCode2HttpClient.AllowAll;
        OpenCode2HarnessRuntime.NeedsStepToolDeny(rules, serverHasStepTool, workflowsOnNow, workflowStep).ShouldBe(add);
    }
}
