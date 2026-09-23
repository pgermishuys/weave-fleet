extern alias FakeLlm;

using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode2;

/// <summary>
/// The workflow step tool on OpenCode 2: every session on a server with workflows on that isn't a step is created with a
/// rule denying <c>fleet_step_done</c> after its allow-all rule, and V2 then leaves the tool out of what the model is
/// offered. A step session sees it and finishes its step with it. A session made before the switch gets the rule when
/// it's resumed. These fail if a future V2 changes how a session's rules hide a tool.
/// </summary>
public sealed partial class OpenCode2LiveTests
{
    private const string StepWorkflow = """
        name: Live check
        steps:
          - id: check
            title: Check
            model: standard
            prompt: Check it.
            outcomes: [pass, changes]
        """;

    [OpenCode2Fact]
    public async Task A_step_session_finishes_its_step_with_fleet_step_done_and_other_sessions_never_see_it()
    {
        const string normalPrompt = "What does this folder hold? (workflows: normal)";
        const string stepPrompt = "Check the change, then finish the step. (workflows: step)";
        fleet.Answer(request => LlmRequest.Starts(request, normalPrompt) ? new ScriptedLlmResponse { Text = "Files." } : null);
        fleet.Answer(request => LlmRequest.Starts(request, stepPrompt)
            ? ToolCall("call_step_done", FleetWorkflows.StepTool, new { outcome = "pass", summary = "Checked: the change is right." })
            : LlmRequest.Continues(request, stepPrompt) ? new ScriptedLlmResponse { Text = "Done." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("workflow-steps");

        await SetWorkflowsAsync(true);
        try
        {
            var normal = await fleet.CreateSessionAsync(folder, "Ordinary session", cts.Token);
            var runId = await SeedRunAsync(folder, StepWorkflow, "check");
            var step = await CreateStepSessionAsync(folder, runId, "check", userFinishes: false, cts.Token);

            var events = fleet.Watch(cts.Token, normal, step);
            await PromptAsync(normal, normalPrompt, options: null, cts.Token);
            await PromptAsync(step, stepPrompt, options: null, cts.Token);
            await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Continues(r, stepPrompt))
                && fleet.Llm.Queue.Requests.Any(r => LlmRequest.Starts(r, normalPrompt)), cts.Token);

            var normalTools = LlmRequest.OfferedToolNames(fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, normalPrompt)));
            normalTools.ShouldContain("fleet_canvas_list");
            normalTools.ShouldNotContain(FleetWorkflows.StepTool);
            LlmRequest.OfferedToolNames(fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, stepPrompt))).ShouldContain(FleetWorkflows.StepTool);
            LlmRequest.LastToolText(fleet.Llm.Queue.Requests.First(r => LlmRequest.Continues(r, stepPrompt)))
                .ShouldNotBeNull().ShouldContain("Recorded outcome pass.");

            await WaitForAsync(events, async () => (await RunAsync(runId))?.Status == WorkflowRunStatus.Done, cts.Token);
        }
        finally
        {
            await SetWorkflowsAsync(false);
        }
    }

    private const string SkillWorkflow = """
        name: Live skills
        steps:
          - id: check
            title: Check
            model: standard
            prompt: Check it.
            outcomes: [pass]
          - id: review
            title: Review
            model: standard
            skill: fleet-code-review
            prompt: Review it. (workflows: skill-off)
            outcomes: [pass]
        """;

    /// <summary>Check finishes on V2; Review's built-in skill is off, so Fleet never starts Review's session without it.</summary>
    [OpenCode2Fact]
    public async Task A_step_whose_built_in_skill_is_off_waits_before_its_session_starts()
    {
        const string checkPrompt = "Check the change, then finish the step. (workflows: skill-off)";
        fleet.Answer(request => LlmRequest.Starts(request, checkPrompt)
            ? ToolCall("call_check_done", FleetWorkflows.StepTool, new { outcome = "pass", summary = "Checked." })
            : LlmRequest.Continues(request, checkPrompt) ? new ScriptedLlmResponse { Text = "Done." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("workflow-skill-off");

        await SetWorkflowsAsync(true);
        try
        {
            var runId = await SeedRunAsync(folder, SkillWorkflow, "check");
            var step = await CreateStepSessionAsync(folder, runId, "check", userFinishes: false, cts.Token);
            var events = fleet.Watch(cts.Token, step);
            await PromptAsync(step, checkPrompt, options: null, cts.Token);

            await WaitForAsync(events, async () => (await RunAsync(runId)) is { Status: WorkflowRunStatus.Waiting, CurrentStepId: "review" }, cts.Token);
            var run = (await RunAsync(runId)).ShouldNotBeNull();
            (run.WaitingKind, run.WaitingReason).ShouldBe((WorkflowWaitingKinds.SkillOff, "Review uses fleet-code-review, which is now off."));
            var review = (await StepsAsync(runId)).Single(v => v.StepId == "review");
            (review.Status, review.SessionId).ShouldBe((WorkflowRunStepStatus.Waiting, null));

            var retried = await fleet.Services.GetRequiredService<WorkflowRunner>().AnswerAsync(OpenCode2LiveFleet.Owner, runId, WorkflowRunner.RetryChoice, null, null, cts.Token);
            retried.IsFailure.ShouldBeTrue();
            retried.Error.Description.ShouldBe("fleet-code-review is still off. Turn it on in Settings → Skills, then retry.");
            fleet.Llm.Queue.Requests.ShouldNotContain(r => LlmRequest.FirstUserText(r) != null && LlmRequest.FirstUserText(r)!.StartsWith("Review it.", StringComparison.Ordinal));
        }
        finally
        {
            await SetWorkflowsAsync(false);
        }
    }

    private const string TogetherWorkflow = """
        name: Live together
        steps:
          - id: talk
            title: Talk
            model: standard
            finish: you
            writes: [notes.md]
            prompt: Talk it through.
            outcomes: [ready]
          - id: approve
            title: Approve
            you: Keep it?
            choices:
              Keep: end
        """;

    [OpenCode2Fact]
    public async Task A_step_you_finish_has_no_step_tool_and_its_summary_is_the_reply_to_the_wrap_up()
    {
        const string talkPrompt = "Let's talk it through. (workflows: together)";
        const string wrapUpStart = "The user is moving on to Approve.";
        fleet.Answer(request => LlmRequest.Starts(request, talkPrompt) ? new ScriptedLlmResponse { Text = "Here's a first idea." } : null);
        fleet.Answer(request => LlmRequest.Starts(request, wrapUpStart)
            ? new ScriptedLlmResponse { Text = "Summary for Approve: notes.md has the idea." }
            : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("workflow-together");
        WorkflowLiveGit.Init(folder);
        File.WriteAllText(Path.Combine(folder, "notes.md"), "# Notes");

        await SetWorkflowsAsync(true);
        try
        {
            var runId = await SeedRunAsync(folder, TogetherWorkflow, "talk");
            var step = await CreateStepSessionAsync(folder, runId, "talk", userFinishes: true, cts.Token);

            var events = fleet.Watch(cts.Token, step);
            await PromptAsync(step, talkPrompt, options: null, cts.Token);
            await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Any(r => LlmRequest.Starts(r, talkPrompt)), cts.Token);

            // Made like any session that isn't a step: the rule hides the tool.
            var tools = LlmRequest.OfferedToolNames(fleet.Llm.Queue.Requests.First(r => LlmRequest.Starts(r, talkPrompt)));
            tools.ShouldContain("fleet_canvas_list");
            tools.ShouldNotContain(FleetWorkflows.StepTool);
            await WaitForAsync(events, async () => !SessionActivityTracker.IsInTurn(fleet.Services.GetRequiredService<SessionActivityTracker>().Get(step)?.ActivityStatus)
                && (await RunAsync(runId))?.Status == WorkflowRunStatus.Running, cts.Token);

            var moved = await fleet.Services.GetRequiredService<WorkflowRunner>().MoveOnAsync(OpenCode2LiveFleet.Owner, runId, null, "Keep it short.", cts.Token);
            moved.IsSuccess.ShouldBeTrue(moved.IsFailure ? moved.Error.Description : null);

            // The run moves on when the turn answering the wrap-up ends, and its reply is the summary.
            await WaitForAsync(events, async () => (await RunAsync(runId))?.CurrentStepId == "approve", cts.Token);
            var visit = (await StepsAsync(runId)).Single(v => v.StepId == "talk");
            (visit.Status, visit.Outcome, visit.Summary, visit.FilesChecked)
                .ShouldBe((WorkflowRunStepStatus.Done, "ready", "Summary for Approve: notes.md has the idea.", true));

            // Past the files check, Fleet committed the declared file.
            (visit.FilesCommit, visit.FilesCommitError).ShouldBe((WorkflowLiveGit.Run(folder, "rev-parse", "--short", "HEAD").Trim(), null));
            WorkflowLiveGit.Run(folder, "log", "-1", "--format=%s").Trim().ShouldBe("docs: talk (notes.md)");
            WorkflowLiveGit.Run(folder, "status", "--porcelain", "--", "notes.md").ShouldBeEmpty();
            var wrapUp = fleet.Llm.Queue.Requests.Select(LlmRequest.LastUserText).First(t => t?.StartsWith(wrapUpStart, StringComparison.Ordinal) == true);
            wrapUp.ShouldBe("The user is moving on to Approve. Update notes.md with everything agreed in this conversation, then reply with a short summary for Approve.\n\nTheir note: Keep it short.");
            fleet.Llm.Queue.Requests.Where(r => LlmRequest.LastUserText(r) == wrapUp).ShouldAllBe(r => !LlmRequest.OfferedToolNames(r).Contains(FleetWorkflows.StepTool));
        }
        finally
        {
            await SetWorkflowsAsync(false);
        }
    }

    [OpenCode2Fact]
    public async Task A_session_made_before_workflows_were_on_gets_the_step_tool_denied_when_its_resumed()
    {
        const string prompt = "Still there? (workflows: resumed)";
        fleet.Answer(request => LlmRequest.Starts(request, prompt) ? new ScriptedLlmResponse { Text = "Yes." } : null);
        using var cts = new CancellationTokenSource(Timeout);
        var folder = fleet.NewFolder("workflow-resumed");

        var before = await fleet.CreateSessionAsync(folder, "Made before the switch", cts.Token);
        var token = ((OpenCode2HarnessSession)await fleet.HarnessSessionAsync(before, cts.Token)).ResumeToken;

        await SetWorkflowsAsync(true);
        try
        {
            // What Fleet does after a restart: the session is resumed on the owner's server as it now is.
            await using var resumed = await fleet.Runtime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = before,
                WorkingDirectory = folder,
                OwnerUserId = OpenCode2LiveFleet.Owner,
                ResumeToken = token!,
            }, cts.Token);

            var asked = fleet.Llm.Queue.Requests.Count;
            await resumed.SendPromptAsync(prompt, null, cts.Token);
            var events = fleet.Watch(cts.Token, before);
            await WaitForAsync(events, () => fleet.Llm.Queue.Requests.Skip(asked).Any(r => LlmRequest.Starts(r, prompt)), cts.Token);

            var tools = LlmRequest.OfferedToolNames(fleet.Llm.Queue.Requests.Skip(asked).First(r => LlmRequest.Starts(r, prompt)));
            tools.ShouldContain("fleet_canvas_list");
            tools.ShouldNotContain(FleetWorkflows.StepTool);

            // It ran on a server that has the step tool: the rule hid it, not the server.
            var server = ((OpenCode2HarnessSession)resumed).ProcessId.ShouldNotBeNull();
            if (OperatingSystem.IsLinux())
                File.ReadAllText($"/proc/{server}/environ").Split('\0').ShouldContain($"{FleetWorkflows.EnvironmentVariable}=1");
        }
        finally
        {
            await SetWorkflowsAsync(false);
        }
    }

    /// <summary>Turns workflows on or off as Settings does; the owner's next session gets a server to match.</summary>
    private async Task SetWorkflowsAsync(bool enabled)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(FleetWorkflows.PreferenceKey, enabled ? "true" : "false");
    }

    /// <summary>A run, as the runner saves one, whose first step the next session will be.</summary>
    private async Task<string> SeedRunAsync(string folder, string definition, string firstStep)
    {
        var runId = $"run-{Guid.NewGuid():N}";
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().InsertAsync(new WorkflowRun
        {
            Id = runId,
            UserId = OpenCode2LiveFleet.Owner,
            WorkflowId = "repo:live",
            WorkflowName = "Live check",
            Definition = definition,
            Request = "Check it",
            Slug = "check-it",
            Title = "Check it",
            RepositoryPath = folder,
            WorktreePath = folder,
            HarnessType = OpenCode2HarnessSession.Type,
            Status = WorkflowRunStatus.Running,
            CurrentStepId = firstStep,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
        });
        return runId;
    }

    /// <summary>Starts the step's session the way the runner does, and records it as the running step.</summary>
    private async Task<string> CreateStepSessionAsync(string folder, string runId, string stepId, bool userFinishes, CancellationToken ct)
    {
        var created = await fleet.WithOrchestratorAsync(orchestrator => orchestrator.CreateSessionAsync(
            new CreateSessionRequest
            {
                Directory = folder,
                Title = $"Check it · {stepId}",
                HarnessType = OpenCode2HarnessSession.Type,
                WorkflowRunId = runId,
                WorkflowUserFinishes = userFinishes,
            }, ct));
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.Description : null);

        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().InsertStepAsync(new WorkflowRunStep
        {
            Id = $"visit-{Guid.NewGuid():N}",
            RunId = runId,
            StepId = stepId,
            SessionId = created.Value.Session.Id,
            Status = WorkflowRunStepStatus.Running,
            Finish = userFinishes ? WorkflowFinishers.You : WorkflowFinishers.Agent,
            StartedAt = DateTime.UtcNow.ToString("O"),
        });
        return created.Value.Session.Id;
    }

    private async Task<IReadOnlyList<WorkflowRunStep>> StepsAsync(string runId)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().ListStepsAsync(runId);
    }

    private async Task<WorkflowRun?> RunAsync(string runId)
    {
        using var user = BackgroundUserContext.BeginScope(OpenCode2LiveFleet.Owner);
        using var scope = fleet.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>().GetAsync(runId);
    }
}
