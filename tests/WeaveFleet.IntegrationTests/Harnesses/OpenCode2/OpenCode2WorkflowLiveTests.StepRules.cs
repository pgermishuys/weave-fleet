extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// Who may finish a workflow step on OpenCode 2, beyond the step session itself: not its subagent, not a delegated
/// child Fleet prompts again (which keeps the rules it was started with), and no session at all with workflows off.
/// Ported from OpenCode 1's <c>WorkflowStepToolLiveTests</c>, which were the only cover for these.
/// </summary>
public sealed partial class OpenCode2WorkflowLiveTests
{
    [OpenCode2Fact]
    public async Task A_step_sessions_subagent_cant_finish_the_step()
    {
        const string stepPrompt = "Ask a subagent to finish the step. (workflows: delegating step)";
        const string childPrompt = "Finish the step for your parent. (workflows: step child)";
        fleet.Answer(request => LlmRequest.Starts(request, childPrompt)
            ? ToolCall("call_child_done", FleetWorkflows.StepTool, new { outcome = "pass", summary = "From the child." })
            : LlmRequest.Continues(request, childPrompt) ? new ScriptedLlmResponse { Text = "I couldn't." }
            : LlmRequest.Starts(request, stepPrompt)
                ? ToolCall("call_step_child", "subagent", new { description = "Finish the step", prompt = childPrompt, agent = "general", subagent_type = "general" })
            : LlmRequest.Continues(request, stepPrompt) ? new ScriptedLlmResponse { Text = "The helper couldn't finish it." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("workflow-step-child");

        await SetWorkflowsAsync(true);
        try
        {
            var runId = await SeedRunAsync(folder, StepWorkflow, "check");
            var step = await CreateStepSessionAsync(folder, runId, "check", userFinishes: false, cts.Token);
            var events = fleet.Watch(cts.Token, step);

            await PromptAsync(step, stepPrompt, options: null, cts.Token);
            await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Continues(r, stepPrompt)), cts.Token);

            // The child's call came back refused, and the step is still running.
            LlmRequest.LastToolText(fleet.Llm.Queue.Requests.First(r => LlmRequest.Continues(r, childPrompt)))
                .ShouldNotBeNull().ShouldContain("Only the session Fleet started for a workflow step can finish it.");
            (await RunAsync(runId)).ShouldNotBeNull().Status.ShouldBe(WorkflowRunStatus.Running);
            (await StepsAsync(runId)).ShouldHaveSingleItem().Status.ShouldBe(WorkflowRunStepStatus.Running);
        }
        finally
        {
            await SetWorkflowsAsync(false);
        }
    }

    [OpenCode2Fact]
    public async Task A_delegated_child_keeps_the_rules_its_agent_gave_it_when_Fleet_prompts_it()
    {
        const string prompt = "Ask a subagent to look around. (workflows: delegating)";
        const string lookPrompt = "Look around the folder. (workflows: look)";
        const string againPrompt = "And once more. (workflows: look again)";
        fleet.Answer(request => LlmRequest.Starts(request, againPrompt) || LlmRequest.Starts(request, lookPrompt) ? new ScriptedLlmResponse { Text = "Looked." }
            : LlmRequest.Starts(request, prompt)
                ? ToolCall("call_look", "subagent", new { description = "Look around", prompt = lookPrompt, agent = "general", subagent_type = "general" })
            : LlmRequest.Continues(request, prompt) ? new ScriptedLlmResponse { Text = "It looked." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("workflow-child-rules");

        await SetWorkflowsAsync(true);
        try
        {
            var normal = await fleet.CreateSessionAsync(folder, "Ordinary session", cts.Token);
            var events = fleet.Watch(cts.Token, normal);
            await PromptAsync(normal, prompt, options: null, cts.Token);
            var childId = await WaitForAsync(events, async () =>
                (await Delegations(normal)).FirstOrDefault(d => d.ParentToolCallId == "call_look" && d.Status == "completed")?.ChildSessionId, cts.Token);
            await WaitForAsync(events, () => events.For(normal).Any(e => e.Type == "session.idle"), cts.Token);
            var first = fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, lookPrompt));

            // Fleet prompts the child as the user would.
            var childEvents = fleet.Watch(cts.Token, childId);
            await PromptAsync(childId, againPrompt, options: null, cts.Token);
            await WaitForAsync(childEvents, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Starts(r, againPrompt)), cts.Token);
            var again = fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, againPrompt));

            // Rules of Fleet's own would replace the ones the subagent was started with, and what it's offered would change.
            LlmRequest.OfferedToolNames(again).ShouldBe(LlmRequest.OfferedToolNames(first), ignoreOrder: true);
            // It follows its parent's rules, so it never had the step tool either.
            LlmRequest.OfferedToolNames(first).ShouldNotContain(FleetWorkflows.StepTool);
            LlmRequest.OfferedToolNames(again).ShouldNotContain(FleetWorkflows.StepTool);
        }
        finally
        {
            await SetWorkflowsAsync(false);
        }
    }

    [OpenCode2Fact]
    public async Task With_workflows_off_no_session_has_the_step_tool()
    {
        const string normalPrompt = "What does this folder hold? (workflows off: normal)";
        const string stepPrompt = "Check the change, then finish the step. (workflows off: step)";
        fleet.Answer(request => LlmRequest.Starts(request, normalPrompt) || LlmRequest.Starts(request, stepPrompt) ? new ScriptedLlmResponse { Text = "Files." } : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("workflows-off");

        await SetWorkflowsAsync(false);
        var normal = await fleet.CreateSessionAsync(folder, "Ordinary session", cts.Token);
        var runId = await SeedRunAsync(folder, StepWorkflow, "check");
        var step = await CreateStepSessionAsync(folder, runId, "check", userFinishes: false, cts.Token);
        var events = fleet.Watch(cts.Token, normal, step);

        await PromptAsync(normal, normalPrompt, options: null, cts.Token);
        await PromptAsync(step, stepPrompt, options: null, cts.Token);
        await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Starts(r, normalPrompt))
            && fleet.Llm.Queue.Requests.Any(r => LlmRequest.Starts(r, stepPrompt)), cts.Token);

        foreach (var prompt in new[] { normalPrompt, stepPrompt })
        {
            var tools = LlmRequest.OfferedToolNames(fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, prompt)));
            tools.ShouldContain("fleet_canvas_list", prompt);
            tools.ShouldNotContain(FleetWorkflows.StepTool, prompt);
        }
    }
}
