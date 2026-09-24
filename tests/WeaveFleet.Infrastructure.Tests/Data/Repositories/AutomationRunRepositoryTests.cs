using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class AutomationRunRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    private static async Task<(SqliteConnection Keeper, IDbConnectionFactory Factory, AutomationRunRepository Repo)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        return (keeper, factory, new AutomationRunRepository(factory, new TestUserContext(OwnerId)));
    }

    private static AutomationRun Run(string id, string automationId, string startedAt, string status = "started", string? scheduledFor = null, string userId = OwnerId) => new()
    {
        Id = id,
        AutomationId = automationId,
        UserId = userId,
        Trigger = scheduledFor is null ? "manual" : "schedule",
        ScheduledFor = scheduledFor,
        StartedAt = startedAt,
        Status = status,
        SessionId = status == "started" ? $"session-{id}" : null,
    };

    [Fact]
    public async Task A_run_round_trips_and_is_completed_with_its_session()
    {
        var (keeper, _, repo) = await CreateAsync();
        using var _ = keeper;
        await repo.InsertAsync(Run("run-1", "auto-1", "2026-09-14T09:00:01.0000000Z", status: "starting", scheduledFor: "2026-09-14T09:00:00.0000000Z"));

        await repo.CompleteAsync("run-1", "started", "session-9", "instance-9", null);

        var run = (await repo.ListByAutomationAsync("auto-1", 10)).ShouldHaveSingleItem();
        (run.Trigger, run.Status, run.SessionId, run.InstanceId, run.UserId).ShouldBe(("schedule", "started", "session-9", "instance-9", OwnerId));
        run.ScheduledFor.ShouldBe("2026-09-14T09:00:00.0000000Z");
    }

    [Fact]
    public async Task Runs_list_newest_first_and_the_latest_is_kept_per_automation()
    {
        var (keeper, _, repo) = await CreateAsync();
        using var _ = keeper;
        await repo.InsertAsync(Run("run-1", "auto-1", "2026-09-07T09:00:00.0000000Z"));
        await repo.InsertAsync(Run("run-2", "auto-1", "2026-09-14T09:00:00.0000000Z", status: "failed"));
        await repo.InsertAsync(Run("run-3", "auto-2", "2026-09-10T09:00:00.0000000Z"));
        await repo.InsertAsync(Run("run-4", "auto-3", "2026-09-10T09:00:00.0000000Z", userId: "someone-else"));

        (await repo.ListByAutomationAsync("auto-1", 10)).Select(r => r.Id).ShouldBe(["run-2", "run-1"]);
        (await repo.ListByAutomationAsync("auto-1", 1)).ShouldHaveSingleItem().Id.ShouldBe("run-2");

        var latest = await repo.GetLatestPerAutomationAsync();
        latest.Keys.Order().ShouldBe(["auto-1", "auto-2"]);
        latest["auto-1"].Id.ShouldBe("run-2");
    }

    [Fact]
    public async Task The_last_scheduled_occurrence_is_kept_per_automation()
    {
        var (keeper, _, repo) = await CreateAsync();
        using var _ = keeper;
        await repo.InsertAsync(Run("run-1", "auto-1", "2026-09-07T09:00:05.0000000Z", scheduledFor: "2026-09-07T09:00:00.0000000Z"));
        await repo.InsertAsync(Run("run-2", "auto-1", "2026-09-14T12:00:00.0000000Z", status: "skipped", scheduledFor: "2026-09-14T09:00:00.0000000Z"));
        await repo.InsertAsync(Run("run-3", "auto-1", "2026-09-15T08:00:00.0000000Z"));

        var last = await repo.GetLastScheduledForAsync();

        last.ShouldHaveSingleItem().ShouldBe(new KeyValuePair<string, string>("auto-1", "2026-09-14T09:00:00.0000000Z"));
    }

    [Fact]
    public async Task Runs_stuck_starting_are_failed_with_a_reason()
    {
        var (keeper, _, repo) = await CreateAsync();
        using var _ = keeper;
        await repo.InsertAsync(Run("run-old", "auto-1", "2026-09-14T08:00:00.0000000Z", status: "starting"));
        await repo.InsertAsync(Run("run-new", "auto-1", "2026-09-14T08:59:00.0000000Z", status: "starting"));

        (await repo.FailStaleStartingAsync("2026-09-14T08:50:00.0000000Z", "Fleet stopped before the run started.")).ShouldBe(1);

        var runs = await repo.ListByAutomationAsync("auto-1", 10);
        runs.Single(r => r.Id == "run-old").Error.ShouldBe("Fleet stopped before the run started.");
        runs.Single(r => r.Id == "run-new").Status.ShouldBe("starting");
    }

    [Fact]
    public async Task An_automation_keeps_where_its_runs_happen()
    {
        var (keeper, factory, _) = await CreateAsync();
        using var _ = keeper;
        var automations = new AutomationRepository(factory, new TestUserContext(OwnerId));
        await automations.InsertAsync(new Automation
        {
            Id = "auto-1",
            Name = "Weekly digest",
            Prompt = "Summarise the open PRs",
            TriggerType = "once",
            TriggerConfig = "2026-09-21T09:00",
            WorkspaceId = "/home/me/source/weave-fleet",
            Isolation = "worktree",
            BaseBranch = "origin/main",
            CreatedAt = "2026-09-14T09:00:00.0000000Z",
        });

        var stored = (await automations.GetByIdAsync("auto-1")).ShouldNotBeNull();
        (stored.Isolation, stored.BaseBranch).ShouldBe(("worktree", "origin/main"));

        stored.Isolation = "existing";
        stored.BaseBranch = null;
        await automations.UpdateAsync(stored);
        var updated = (await automations.GetByIdAsync("auto-1")).ShouldNotBeNull();
        (updated.Isolation, updated.BaseBranch).ShouldBe(("existing", (string?)null));
    }

    [Fact]
    public async Task An_automation_keeps_the_workflow_it_runs_and_its_runs_keep_the_workflow_run()
    {
        var (keeper, factory, repo) = await CreateAsync();
        using var _ = keeper;
        var automations = new AutomationRepository(factory, new TestUserContext(OwnerId));
        await automations.InsertAsync(new Automation
        {
            Id = "auto-1",
            Name = "Weekly dependency bump",
            Prompt = "Bump the client's dependencies",
            TriggerType = "schedule",
            TriggerConfig = "0 9 * * 1",
            WorkspaceId = "/home/me/source/weave-fleet",
            Isolation = "worktree",
            TargetType = "workflow",
            WorkflowId = "builtin:build-a-feature",
            WorkflowSteps = ["design", "verify"],
            HarnessType = "opencode2",
            CreatedAt = "2026-09-23T09:00:00.0000000Z",
        });

        var stored = (await automations.GetByIdAsync("auto-1")).ShouldNotBeNull();
        (stored.TargetType, stored.WorkflowId, stored.HarnessType).ShouldBe(("workflow", "builtin:build-a-feature", "opencode2"));
        stored.WorkflowSteps.ShouldBe(["design", "verify"]);

        stored.WorkflowId = "repo:deps";
        stored.WorkflowSteps = [];
        await automations.UpdateAsync(stored);
        var updated = (await automations.GetByIdAsync("auto-1")).ShouldNotBeNull();
        (updated.WorkflowId, updated.WorkflowSteps.Count).ShouldBe(("repo:deps", 0));

        // A run that started a workflow run, and one skipped with the reason.
        await repo.InsertAsync(Run("run-1", "auto-1", "2026-09-23T09:00:01.0000000Z", status: "starting"));
        await repo.CompleteAsync("run-1", "started", "step-session", null, null, workflowRunId: "wf-run-1");
        await repo.InsertAsync(Run("run-2", "auto-1", "2026-09-30T09:00:01.0000000Z", status: "starting"));
        await repo.CompleteAsync("run-2", "skipped", null, null, "Skipped: the last run is still waiting on you (Approve the plan).");

        var runs = await repo.ListByAutomationAsync("auto-1", 10);
        (runs[1].Status, runs[1].SessionId, runs[1].WorkflowRunId).ShouldBe(("started", "step-session", "wf-run-1"));
        (runs[0].Status, runs[0].WorkflowRunId, runs[0].Error).ShouldBe(("skipped", (string?)null, "Skipped: the last run is still waiting on you (Approve the plan)."));
        (await repo.GetLatestPerAutomationAsync())["auto-1"].Id.ShouldBe("run-2");
    }
}
