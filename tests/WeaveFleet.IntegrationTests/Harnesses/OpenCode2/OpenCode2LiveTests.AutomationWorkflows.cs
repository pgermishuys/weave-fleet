extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// An automation that runs a workflow, on OpenCode 2 and a scripted model, the whole way: the firing starts a run
/// through the Run box's start path, the step session gets the automation's message as the request and finishes with
/// <c>fleet_step_done</c>, and the run stops at its You decide step. A second firing while it waits there is skipped
/// with the step's name, Run now isn't and gets a worktree of its own, and with Workflows off a firing says so.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    private const string AutomationWorkflow = """
        name: Live automation
        steps:
          - id: check
            title: Check
            model: standard
            prompt: |
              Check: {{request}}
            outcomes: [pass]
          - id: approve
            title: Approve
            you: Keep it?
            choices:
              Keep: end
        """;

    [OpenCode2Fact]
    public async Task An_automation_runs_its_workflow_with_its_message_as_the_request_and_never_stacks_runs()
    {
        const string message = "Bump the client's dependencies (automation workflow, OpenCode 2)";
        fleet.Answer(request => LlmRequest.Starts(request, message)
            ? ToolCall("call_auto_done", FleetWorkflows.StepTool, new { outcome = "pass", summary = "Bumped." })
            : LlmRequest.Continues(request, message) ? new ScriptedLlmResponse { Text = "Done." }
            : null);
        using var cts = new CancellationTokenSource(Timeout * 2);
        var folder = fleet.NewFolder("automation-workflow");
        Directory.CreateDirectory(Path.Combine(folder, ".weave", "workflows"));
        File.WriteAllText(Path.Combine(folder, ".weave", "workflows", "auto.yaml"), AutomationWorkflow);
        WorkflowLiveGit.Init(folder);
        WorkflowLiveGit.Run(folder, "add", ".");
        WorkflowLiveGit.Run(folder, "commit", "--quiet", "-m", "workflow");

        await SetWorkflowsAsync(true);
        try
        {
            var automation = await AsOwnerAsync(scope => scope.GetRequiredService<AutomationService>().CreateAsync(
                "Weekly dependency bump", message, "schedule", "0 9 * * 1", 1, 10, 30,
                workspaceId: folder, targetType: AutomationTargets.Workflow, workflowId: "repo:auto",
                harnessType: OpenCode2HarnessSession.Type));
            automation.IsSuccess.ShouldBeTrue(automation.IsFailure ? automation.Error.Description : null);
            var events = fleet.WatchTopics(["sessions"], cts.Token);

            var fired = await FireAsync(automation.Value, "schedule");
            fired.Status.ShouldBe(AutomationRunStatus.Started, fired.Error);
            var runId = fired.WorkflowRunId.ShouldNotBeNull();

            // The step finishes with the tool, and the run stops at Approve, waiting on the user.
            var waiting = await WaitForAsync(events, async () => await RunAsync(runId) is { Status: WorkflowRunStatus.Waiting } run ? run : null, cts.Token);
            (waiting.CurrentStepId, waiting.Request, waiting.AutomationName).ShouldBe(("approve", message, "Weekly dependency bump"));
            WorkflowRunOptions.Read(waiting.Options).CheckWithMe.ShouldBeFalse();
            var stepRequest = fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, message));
            LlmRequest.LastUserText(stepRequest).ShouldNotBeNull().ShouldStartWith($"Check: {message}");
            LlmRequest.OfferedToolNames(stepRequest).ShouldContain(FleetWorkflows.StepTool);
            (await StepsAsync(runId)).Single(s => s.StepId == "check").Outcome.ShouldBe("pass");
            fired.SessionId.ShouldBe((await StepsAsync(runId)).Single(s => s.StepId == "check").SessionId);
            (await AsOwnerAsync(scope => scope.GetRequiredService<AutomationRunService>().StateOfAsync(fired))).ShouldBe(AutomationRunState.Waiting);

            // A second firing while it waits is skipped, with where it waits.
            var second = await FireAsync(automation.Value, "schedule");
            second.Status.ShouldBe(AutomationRunStatus.Skipped);
            second.Error.ShouldBe("Skipped: the last run is still waiting on you (Approve).");

            // Run now isn't skipped, and its run gets its own worktree and branch.
            var now = await FireAsync(automation.Value, AutomationRunTrigger.Manual);
            now.Status.ShouldBe(AutomationRunStatus.Started, now.Error);
            var nowRun = await WaitForAsync(events, async () => await RunAsync(now.WorkflowRunId!) is { Status: WorkflowRunStatus.Waiting } run ? run : null, cts.Token);
            nowRun.Branch.ShouldNotBeNull().ShouldNotBe(waiting.Branch);
            nowRun.WorktreePath.ShouldNotBe(waiting.WorktreePath);

            // With Workflows off, a firing is skipped and says why.
            await SetWorkflowsAsync(false);
            var off = await FireAsync(automation.Value, AutomationRunTrigger.Manual);
            off.Error.ShouldBe("Skipped: Workflows are turned off in Settings.");
        }
        finally
        {
            await SetWorkflowsAsync(false);
        }
    }

    private Task<AutomationRun> FireAsync(Automation automation, string trigger)
        => AsOwnerAsync(scope => scope.GetRequiredService<AutomationRunService>().RunAsync(automation, new AutomationRunTrigger(trigger, DateTime.UtcNow)));

    private async Task<T> AsOwnerAsync<T>(Func<IServiceProvider, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await call(scope.ServiceProvider);
    }
}
