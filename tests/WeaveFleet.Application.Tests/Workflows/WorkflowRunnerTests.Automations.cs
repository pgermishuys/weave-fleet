using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Skills;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;
using WeaveFleet.Testing.Fixtures;

namespace WeaveFleet.Application.Tests.Workflows;

/// <summary>
/// Automations that run a workflow: a firing starts a run through the Run box's own start path (the real
/// <see cref="WorkflowService"/>, over a real git repository), with the automation's message as the request and Check
/// with me off; a firing that can't or shouldn't start one is skipped, with the reason.
/// </summary>
public sealed partial class WorkflowRunnerTests
{
    private const string AutomationPrompt = "Bump the client's dependencies and fix what breaks";

    private sealed class AutomationRig : IDisposable
    {
        public required RealGitRepository Repository { get; init; }
        public required AutomationRunService Runs { get; init; }
        public required InMemoryAutomationRunRepository RunRows { get; init; }
        public required FakeHarnessRuntime Harness { get; init; }
        public required WorkflowService Workflows { get; init; }

        public void Dispose() => Repository.Dispose();
    }

    private AutomationRig NewAutomationRig()
    {
        var repository = new RealGitRepository();
        var user = new TestUserContext(UserId);
        var roots = new InMemoryWorkspaceRootRepository();
        roots.Seed(new WorkspaceRoot { Id = "root", Path = repository.ParentPath, CreatedAt = DateTime.UtcNow.ToString("O") });
        var scopes = new ServiceCollection()
            .AddScoped(_ => new WorkspaceRootService(roots, user))
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var registry = new FakeHarnessRegistry();
        registry.Register(new FakeHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsWorkflowSteps = true }));
        var runtime = new FakeHarnessRuntime("opencode");
        registry.Register(runtime);

        var workflows = new WorkflowService(
            _runner,
            _runs,
            new RepositoryService(scopes, NullLogger<RepositoryService>.Instance),
            registry,
            new HarnessCatalogService(registry, user, new FleetOptions(), NullLogger<HarnessCatalogService>.Instance),
            new WorkflowModelRoles(_preferences),
            new SkillCatalog(),
            _preferences,
            user,
            TimeProvider.System);
        var automationWorkflows = new AutomationWorkflows(
            workflows, new WorkflowsFeature(new FleetOptions(), _preferences), _runs, new NoUserScope());

        // The workflow target never touches sessions itself: the run's steps start them.
        var executor = new AutomationExecutionService(null!, null!, NullLogger<AutomationExecutionService>.Instance, automationWorkflows);
        var runRows = new InMemoryAutomationRunRepository();
        return new AutomationRig
        {
            Repository = repository,
            RunRows = runRows,
            Harness = runtime,
            Workflows = workflows,
            Runs = new AutomationRunService(runRows, executor, _activity, TimeProvider.System, NullLogger<AutomationRunService>.Instance, automationWorkflows),
        };
    }

    private void MapRoles(string strong)
        => _preferences.Seed(WorkflowModelRoles.PreferenceKey,
            "{\"opencode\":{\"strong\":{\"model\":\"" + strong + "\"},\"standard\":{\"model\":\"fake/standard\"},\"fast\":{\"model\":\"fake/fast\"}}}");

    private static Automation WorkflowAutomation(AutomationRig rig, params string[] optionalSteps) => new()
    {
        Id = "auto-deps",
        Name = "Weekly dependency bump",
        Prompt = AutomationPrompt,
        TriggerType = "schedule",
        TriggerConfig = "0 9 * * 1",
        MaxConcurrentRuns = 0,
        TargetType = AutomationTargets.Workflow,
        WorkflowId = "builtin:build-a-feature",
        WorkflowSteps = [.. optionalSteps],
        WorkspaceId = rig.Repository.Path,
        Isolation = "worktree",
        BaseBranch = "main",
        UserId = UserId,
    };

    private static AutomationRunTrigger Schedule() => new("schedule", DateTime.UtcNow);

    [Fact]
    public async Task an_automation_starts_its_workflow_with_its_message_as_the_request_and_check_with_me_off()
    {
        using var rig = NewAutomationRig();
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-code-review,fleet-run");
        MapRoles("fake/strong");

        var fired = await rig.Runs.RunAsync(WorkflowAutomation(rig, "verify"), Schedule());

        fired.Status.ShouldBe(AutomationRunStatus.Started, fired.Error);
        var run = _runs.Run(fired.WorkflowRunId.ShouldNotBeNull());
        run.Request.ShouldBe(AutomationPrompt);
        run.RepositoryPath.ShouldBe(rig.Repository.Path);
        (run.BaseBranch, run.HarnessType).ShouldBe(("main", "opencode"));
        var options = WorkflowRunOptions.Read(run.Options);
        options.CheckWithMe.ShouldBeFalse();
        options.OptionalSteps.ShouldBe(["verify"]);
        options.RoleOverrides.ShouldBeEmpty();

        // The automation's message is {{request}} in the first step's prompt, and nothing else is added to it.
        var plan = _sessions.Started.ShouldHaveSingleItem();
        plan.StepId.ShouldBe("plan");
        plan.UserFinishes.ShouldBeFalse();
        plan.Prompt.ShouldStartWith($"Plan {AutomationPrompt}");
        plan.Prompt.ShouldEndWith(FleetWorkflows.Footer(["ready"]));

        // Both links: the automation's run opens the run's first step, and the run says who started it.
        fired.SessionId.ShouldBe(plan.SessionId);
        rig.RunRows.All.ShouldHaveSingleItem().WorkflowRunId.ShouldBe(run.Id);
        (run.AutomationId, run.AutomationName).ShouldBe(("auto-deps", "Weekly dependency bump"));
        _events.Last.StartedBy.ShouldBe(new WorkflowRunStartedByDto("auto-deps", "Weekly dependency bump"));
        (await rig.Workflows.GetRunAsync(run.Id)).Value.StartedBy!.AutomationName.ShouldBe("Weekly dependency bump");
        (await rig.Workflows.ListRunsAsync("builtin:build-a-feature", 10)).ShouldHaveSingleItem().StartedBy.ShouldNotBeNull();
    }

    [Fact]
    public async Task a_run_started_from_the_run_box_says_nobody_started_it()
    {
        using var rig = NewAutomationRig();
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-code-review");

        var started = await rig.Workflows.StartAsync(
            new StartWorkflowRunRequest("builtin:build-a-feature", rig.Repository.Path, "Add a shortcut sheet"), CancellationToken.None);

        started.IsSuccess.ShouldBeTrue(started.IsFailure ? started.Error.Description : null);
        started.Value.StartedBy.ShouldBeNull();
    }

    [Fact]
    public async Task the_model_roles_are_read_when_it_fires_not_when_it_was_saved()
    {
        using var rig = NewAutomationRig();
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-code-review");
        var automation = WorkflowAutomation(rig);
        MapRoles("fake/strong");
        var first = await rig.Runs.RunAsync(automation, new AutomationRunTrigger(AutomationRunTrigger.Manual));

        // Settings → Workflows changes after the automation was saved and after its first run.
        MapRoles("fake/stronger");
        var second = await rig.Runs.RunAsync(automation, new AutomationRunTrigger(AutomationRunTrigger.Manual));

        WorkflowRunOptions.Read(_runs.Run(first.WorkflowRunId!).Options).StepModels["plan"].Model.ShouldBe("fake/strong");
        WorkflowRunOptions.Read(_runs.Run(second.WorkflowRunId!).Options).StepModels["plan"].Model.ShouldBe("fake/stronger");
        _sessions.Started.Select(s => s.Model.Model).ShouldBe(["fake/strong", "fake/stronger"]);
    }

    [Fact]
    public async Task a_firing_while_the_last_run_waits_on_you_is_skipped_with_the_step_it_waits_at()
    {
        using var rig = NewAutomationRig();
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-code-review");
        var automation = WorkflowAutomation(rig);
        var first = await rig.Runs.RunAsync(automation, Schedule());

        // Running: the plan is being written.
        var whileRunning = await rig.Runs.RunAsync(automation, Schedule());
        whileRunning.Status.ShouldBe(AutomationRunStatus.Skipped);
        whileRunning.Error.ShouldBe("Skipped: the last run is still running (Plan).");

        // Waiting on you: the plan is done and waits for approval.
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "The plan.");
        var whileWaiting = await rig.Runs.RunAsync(automation, Schedule());
        whileWaiting.Status.ShouldBe(AutomationRunStatus.Skipped);
        whileWaiting.Error.ShouldBe("Skipped: the last run is still waiting on you (Approve the plan).");
        _runs.Runs.Count.ShouldBe(1);

        // Its state follows the workflow run: Needs you now, Ended once it's ended.
        (await rig.Runs.StateOfAsync(rig.RunRows.All.Single(r => r.Id == first.Id))).ShouldBe(AutomationRunState.Waiting);
        (await rig.Runs.StateOfAsync(rig.RunRows.All.Single(r => r.Id == whileWaiting.Id))).ShouldBe(AutomationRunState.Skipped);

        // Run now isn't skipped: someone asked for it.
        var now = await rig.Runs.RunAsync(automation, new AutomationRunTrigger(AutomationRunTrigger.Manual));
        now.Status.ShouldBe(AutomationRunStatus.Started, now.Error);
        now.WorkflowRunId.ShouldNotBe(first.WorkflowRunId);

        // Once both have ended, the next firing goes ahead.
        (await _runner.EndAsync(UserId, first.WorkflowRunId!)).IsSuccess.ShouldBeTrue();
        (await _runner.EndAsync(UserId, now.WorkflowRunId!)).IsSuccess.ShouldBeTrue();
        (await rig.Runs.StateOfAsync(rig.RunRows.All.Single(r => r.Id == first.Id))).ShouldBe(AutomationRunState.Ended);
        (await rig.Runs.RunAsync(automation, Schedule())).Status.ShouldBe(AutomationRunStatus.Started);
    }

    [Fact]
    public async Task a_firing_while_the_last_run_is_with_you_is_skipped()
    {
        using var rig = NewAutomationRig();
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-code-review,fleet-mockups");
        var automation = WorkflowAutomation(rig, "design");

        await rig.Runs.RunAsync(automation, Schedule());
        var second = await rig.Runs.RunAsync(automation, Schedule());

        // Design is a step you finish, even with Check with me off.
        _sessions.Started.ShouldHaveSingleItem().UserFinishes.ShouldBeTrue();
        second.Error.ShouldBe("Skipped: the last run is still with you (Design).");
    }

    [Fact]
    public async Task a_firing_with_workflows_off_is_skipped_and_says_so()
    {
        using var rig = NewAutomationRig();
        _preferences.Seed(FleetWorkflows.PreferenceKey, "false");

        var fired = await rig.Runs.RunAsync(WorkflowAutomation(rig), Schedule());

        fired.Status.ShouldBe(AutomationRunStatus.Skipped);
        fired.Error.ShouldBe("Skipped: Workflows are turned off in Settings.");
        _runs.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task run_now_with_workflows_off_is_skipped_too()
    {
        using var rig = NewAutomationRig();
        _preferences.Seed(FleetWorkflows.PreferenceKey, "false");

        var fired = await rig.Runs.RunAsync(WorkflowAutomation(rig), new AutomationRunTrigger(AutomationRunTrigger.Manual));

        fired.Error.ShouldBe("Skipped: Workflows are turned off in Settings.");
    }

    [Fact]
    public async Task a_firing_the_start_refuses_is_skipped_with_the_start_error()
    {
        using var rig = NewAutomationRig();

        // A skill that's off.
        var skillOff = await rig.Runs.RunAsync(WorkflowAutomation(rig), Schedule());
        skillOff.Status.ShouldBe(AutomationRunStatus.Skipped);
        skillOff.Error.ShouldBe("Skipped: Review uses fleet-code-review, which is off. Turn it on in Settings → Skills.");

        // A model the harness doesn't offer.
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-code-review");
        MapRoles("fake/strong");
        rig.Harness.CatalogBehavior = (_, _, _) => Task.FromResult<HarnessCatalog?>(new HarnessCatalog { Agents = [], Providers = [] });
        var noModel = await rig.Runs.RunAsync(WorkflowAutomation(rig), Schedule());
        noModel.Error.ShouldBe("Skipped: Plan's model, fake/strong, isn't available on OpenCode. Pick another model for Strong in Settings → Workflows, or in the Run box's Models menu.");

        // A workflow that isn't there any more.
        rig.Harness.CatalogBehavior = null;
        var gone = WorkflowAutomation(rig);
        gone.WorkflowId = "repo:deps";
        (await rig.Runs.RunAsync(gone, Schedule())).Error.ShouldBe("Skipped: Workflow with id 'repo:deps' was not found.");

        // A workflow file with errors.
        Directory.CreateDirectory(Path.Combine(rig.Repository.Path, ".weave", "workflows"));
        File.WriteAllText(Path.Combine(rig.Repository.Path, ".weave", "workflows", "deps.yaml"), "name: Deps\nsteps: []\n");
        (await rig.Runs.RunAsync(gone, Schedule())).Error.ShouldStartWith("Skipped: .weave/workflows/deps.yaml");

        _runs.Runs.ShouldBeEmpty();
    }

    private sealed class SkillCatalog : IBuiltInSkillCatalog
    {
        public IReadOnlyList<BuiltInSkill> Skills { get; } =
        [
            new("fleet-code-review", "Reviews."),
            new("fleet-run", "Runs it."),
            new("fleet-mockups", "Mockups."),
        ];
    }
}
