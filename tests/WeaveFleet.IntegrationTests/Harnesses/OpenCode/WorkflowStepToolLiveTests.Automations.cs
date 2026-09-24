extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// An automation that runs a workflow, on a real pooled <c>opencode</c> and a scripted model, the whole way through
/// Fleet: the firing starts a run through the Run box's start path, the orchestrator starts the step's session in a
/// new worktree, the session gets the automation's message as the request and finishes with <c>fleet_step_done</c>,
/// and the run stops at its You decide step. A second firing while it waits there is skipped with the step's name.
/// </summary>
public sealed partial class WorkflowStepToolLiveTests
{
    private const string AutomationMessage = "AUTOMATION: bump the client's dependencies";

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

    [OpenCodeFact]
    public async Task An_automation_runs_its_workflow_with_its_message_as_the_request_and_never_stacks_runs()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-automation-workflow-live-{Guid.NewGuid():N}");
        var work = Path.Combine(root, "work");
        var repository = Path.Combine(work, "repo");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(Path.Combine(repository, ".weave", "workflows"));
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllText(Path.Combine(repository, ".weave", "workflows", "auto.yaml"), AutomationWorkflow);
        WorkflowLiveGit.Init(repository);
        WorkflowLiveGit.Run(repository, "add", ".");
        WorkflowLiveGit.Run(repository, "commit", "--quiet", "-m", "workflow");

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var plugin = Path.Combine(root, "no-op.ts");
        File.WriteAllText(plugin, "export const NoOp = async () => ({})\n");
        var processEnvironment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llm.BaseUrl, plugin);
        llm.Queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Live automation" };
        for (var i = 0; i < 20; i++)
        {
            llm.Queue.Enqueue(request =>
            {
                if (LastRole(request) == "tool")
                    return new ScriptedLlmResponse { Text = "Done." };
                if (LastUserText(request) is { } text && text.StartsWith($"Check: {AutomationMessage}", StringComparison.Ordinal))
                    return ToolCall("call_auto_done", FleetWorkflows.StepTool, new { outcome = "pass", summary = "Bumped." });
                return new ScriptedLlmResponse { Text = "Nothing to do." };
            });
        }

        var factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath);
        try
        {
            try { _ = factory.Services; }
            catch (InvalidCastException) { /* expected: the base class expects a TestServer */ }

            var services = factory.LiveServices;

            // Sessions the orchestrator starts get the scratch HOME and model, never the real ones.
            services.GetRequiredService<OpenCodeHarnessRuntime>().ProcessEnvironment = processEnvironment;
            await AsOwnerAsync(services, async scope =>
            {
                (await scope.GetRequiredService<WorkspaceRootService>().AddRootAsync(work)).IsSuccess.ShouldBeTrue();
                await scope.GetRequiredService<IUserPreferenceRepository>().SetAsync(FleetWorkflows.PreferenceKey, "true");
                return true;
            });

            var automation = await AsOwnerAsync(services, scope => scope.GetRequiredService<AutomationService>().CreateAsync(
                "Weekly dependency bump", AutomationMessage, "schedule", "0 9 * * 1", 1, 10, 30,
                workspaceId: repository, targetType: AutomationTargets.Workflow, workflowId: "repo:auto", harnessType: "opencode"));
            automation.IsSuccess.ShouldBeTrue(automation.IsFailure ? automation.Error.Description : null);

            var fired = await AsOwnerAsync(services, scope => scope.GetRequiredService<AutomationRunService>()
                .RunAsync(automation.Value, new AutomationRunTrigger("schedule", DateTime.UtcNow), ct));
            fired.Status.ShouldBe(AutomationRunStatus.Started, fired.Error);
            var runId = fired.WorkflowRunId.ShouldNotBeNull();

            try
            {
                var waiting = await WaitForAsync(
                    () => AsOwnerAsync(services, scope => scope.GetRequiredService<IWorkflowRunRepository>().GetAsync(runId)),
                    run => run?.Status == WorkflowRunStatus.Waiting,
                    ct);
                (waiting!.CurrentStepId, waiting.Request, waiting.AutomationName).ShouldBe(("approve", AutomationMessage, "Weekly dependency bump"));
                WorkflowRunOptions.Read(waiting.Options).CheckWithMe.ShouldBeFalse();
                waiting.WorktreePath.ShouldNotBe(repository);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    "Timed out. LLM requests:\n" + string.Join("\n", llm.Queue.Requests.Select(r => $"first={FirstUserText(r)} last={LastRole(r)}:{Trim(LastUserText(r) ?? LastToolText(r))}")));
            }

            var stepTurn = Turns(llm).First(t => FirstUserText(t)?.StartsWith($"Check: {AutomationMessage}", StringComparison.Ordinal) == true);
            OfferedToolNames([stepTurn]).ShouldContain(FleetWorkflows.StepTool);

            // The run moves on as soon as the tool is answered; the model hears the answer a moment later.
            var stepPrompt = FirstUserText(stepTurn)!;
            await WaitForAsync(() => Turns(llm), r => r.Any(t => LastRole(t) == "tool" && FirstUserText(t) == stepPrompt), ct);
            LastToolResult(Turns(llm), stepPrompt).ShouldContain("Recorded outcome pass.");

            // A second firing while it waits at Approve is skipped, with where it waits.
            var second = await AsOwnerAsync(services, scope => scope.GetRequiredService<AutomationRunService>()
                .RunAsync(automation.Value, new AutomationRunTrigger("schedule", DateTime.UtcNow), ct));
            second.Status.ShouldBe(AutomationRunStatus.Skipped);
            second.Error.ShouldBe("Skipped: the last run is still waiting on you (Approve).");
            (await AsOwnerAsync(services, scope => scope.GetRequiredService<AutomationRunService>().StateOfAsync(fired)))
                .ShouldBe(AutomationRunState.Waiting);

            await cts.CancelAsync();
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<T> AsOwnerAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> call)
    {
        using var user = BackgroundUserContext.BeginScope(Owner);
        using var scope = services.CreateScope();
        return await call(scope.ServiceProvider);
    }
}
