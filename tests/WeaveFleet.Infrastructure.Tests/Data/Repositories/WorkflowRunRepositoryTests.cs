using Microsoft.Data.Sqlite;
using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Infrastructure.Data.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Data.Repositories;

public sealed class WorkflowRunRepositoryTests
{
    private const string OwnerId = TestUserContext.DefaultUserId;

    private static async Task<(SqliteConnection Keeper, WorkflowRunRepository Repo, WorkflowRunRepository Someone)> CreateAsync()
    {
        var (keeper, factory) = await TestDbHelper.CreateSharedDbAsync();
        return (keeper, new WorkflowRunRepository(factory, new TestUserContext(OwnerId)), new WorkflowRunRepository(factory, new TestUserContext("someone-else")));
    }

    private static WorkflowRun Run(string id, string createdAt, string status = WorkflowRunStatus.Running) => new()
    {
        Id = id,
        UserId = OwnerId,
        WorkflowId = "builtin:build-a-feature",
        WorkflowName = "Build a feature",
        Definition = "name: Build a feature",
        Request = "Press ? to see every keyboard shortcut",
        Slug = "press-see-every-keyboard-shortcut",
        Title = "Press ? to see every keyboard shortcut",
        RepositoryPath = "/repo",
        BaseBranch = "main",
        HarnessType = "opencode",
        Options = """{"optionalSteps":["design"]}""",
        Status = status,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    [Fact]
    public async Task A_run_and_its_steps_round_trip_and_update()
    {
        var (keeper, repo, _) = await CreateAsync();
        using var _ = keeper;
        var run = Run("run-1", "2026-09-23T10:00:00.0000000Z");
        await repo.InsertAsync(run);

        run.Status = WorkflowRunStatus.Waiting;
        run.CurrentStepId = "ok-plan";
        run.WaitingReason = "Build it this way?";
        run.WorktreePath = "/repo-worktrees/shortcut";
        run.Branch = "fleet/shortcut";
        await repo.UpdateAsync(run);

        var step = new WorkflowRunStep { Id = "v1", RunId = "run-1", StepId = "plan", Visit = 1, SessionId = "s1", Status = "running", StartedAt = "2026-09-23T10:00:01.0000000Z" };
        await repo.InsertStepAsync(step);
        step.Status = WorkflowRunStepStatus.Done;
        step.Outcome = "ready";
        step.Summary = "The plan has four steps.";
        step.FinishedAt = "2026-09-23T10:04:00.0000000Z";
        await repo.UpdateStepAsync(step);

        var saved = (await repo.GetAsync("run-1")).ShouldNotBeNull();
        (saved.Status, saved.CurrentStepId, saved.WaitingReason, saved.WorktreePath, saved.Branch, saved.Options)
            .ShouldBe((WorkflowRunStatus.Waiting, "ok-plan", "Build it this way?", "/repo-worktrees/shortcut", "fleet/shortcut", """{"optionalSteps":["design"]}"""));
        var visit = (await repo.ListStepsAsync("run-1")).ShouldHaveSingleItem();
        (visit.Status, visit.Outcome, visit.Summary, visit.SessionId).ShouldBe((WorkflowRunStepStatus.Done, "ready", "The plan has four steps.", "s1"));
        (await repo.GetStepBySessionAsync("s1")).ShouldNotBeNull().Id.ShouldBe("v1");
    }

    [Fact]
    public async Task A_run_keeps_the_automation_that_started_it()
    {
        var (keeper, repo, _) = await CreateAsync();
        using var _ = keeper;
        var started = Run("run-1", "2026-09-23T10:00:00.0000000Z");
        started.AutomationId = "auto-1";
        started.AutomationName = "Weekly dependency bump";
        await repo.InsertAsync(started);
        await repo.InsertAsync(Run("run-2", "2026-09-23T11:00:00.0000000Z"));

        // An update never loses who started it.
        started.Status = WorkflowRunStatus.Waiting;
        await repo.UpdateAsync(started);

        var saved = (await repo.GetAsync("run-1")).ShouldNotBeNull();
        (saved.AutomationId, saved.AutomationName).ShouldBe(("auto-1", "Weekly dependency bump"));
        var fromTheRunBox = (await repo.GetAsync("run-2")).ShouldNotBeNull();
        (fromTheRunBox.AutomationId, fromTheRunBox.AutomationName).ShouldBe(((string?)null, (string?)null));
    }

    [Fact]
    public async Task A_step_you_finish_and_what_a_run_waits_on_round_trip()
    {
        var (keeper, repo, _) = await CreateAsync();
        using var _ = keeper;
        var run = Run("run-1", "2026-09-23T10:00:00.0000000Z");
        await repo.InsertAsync(run);
        run.Status = WorkflowRunStatus.Waiting;
        run.WaitingKind = "missing-files";
        await repo.UpdateAsync(run);

        var step = new WorkflowRunStep
        {
            Id = "v1", RunId = "run-1", StepId = "design", Visit = 1, SessionId = "s1", Status = WorkflowRunStepStatus.Running,
            Finish = WorkflowFinishers.You, PromptMessageId = "msg_1", StartedAt = "2026-09-23T10:00:01.0000000Z",
        };
        await repo.InsertStepAsync(step);
        step.Status = WorkflowRunStepStatus.WrappingUp;
        step.Outcome = "ready";
        step.WrapUpMessageId = "msg_2";
        step.HandOffNote = "Keep the status-bar shortcuts.";
        step.FilesChecked = true;
        step.FilesCommit = "a1b2c3d";
        step.FilesCommitError = "no email was given and auto-detection is disabled";
        await repo.UpdateStepAsync(step);

        (await repo.GetAsync("run-1")).ShouldNotBeNull().WaitingKind.ShouldBe("missing-files");
        var visit = (await repo.ListStepsAsync("run-1")).ShouldHaveSingleItem();
        (visit.Status, visit.Finish, visit.PromptMessageId, visit.WrapUpMessageId, visit.HandOffNote, visit.FilesChecked)
            .ShouldBe((WorkflowRunStepStatus.WrappingUp, WorkflowFinishers.You, "msg_1", "msg_2", "Keep the status-bar shortcuts.", true));
        (visit.FilesCommit, visit.FilesCommitError).ShouldBe(("a1b2c3d", "no email was given and auto-detection is disabled"));

        // A visit from before this change reads as one the agent finishes, with its files not checked.
        await repo.InsertStepAsync(new WorkflowRunStep { Id = "v0", RunId = "run-1", StepId = "plan", Status = "done", StartedAt = "2026-09-23T09:00:00.0000000Z" });
        var old = (await repo.ListStepsAsync("run-1"))[0];
        (old.Finish, old.FilesChecked, old.FilesCommit, old.FilesCommitError).ShouldBe(((string?)null, false, (string?)null, (string?)null));
    }

    [Fact]
    public async Task Lists_are_the_owners_and_unfinished_runs_span_everyone()
    {
        var (keeper, repo, someone) = await CreateAsync();
        using var _ = keeper;
        await repo.InsertAsync(Run("run-1", "2026-09-23T10:00:00.0000000Z", WorkflowRunStatus.Done));
        await repo.InsertAsync(Run("run-2", "2026-09-23T11:00:00.0000000Z", WorkflowRunStatus.Waiting));

        (await repo.ListAsync(10)).Select(r => r.Id).ShouldBe(["run-2", "run-1"]);
        (await someone.ListAsync(10)).ShouldBeEmpty();
        (await someone.GetAsync("run-1")).ShouldBeNull();
        (await someone.ListUnfinishedAsync()).Select(r => r.Id).ShouldBe(["run-2"]);
    }
}
