using Shouldly;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Workflows;

/// <summary>
/// What the first run on real models found: every step is told the request, Plan decides instead of asking, a step
/// whose file says <c>finish: agent</c> ignores Check with me, and Fleet commits the files a step declares.
/// </summary>
public sealed partial class WorkflowRunnerTests
{
    private const string Request = "Press ? to see every keyboard shortcut";
    private const string Worktree = "/repo-worktrees/press-see-every-keyboard-shortcut";

    // ── The request in every step ──────────────────────────────────────────────

    [Fact]
    public async Task every_agent_step_of_build_a_feature_is_told_the_request()
    {
        var run = await StartAsync(optional: ["design", "verify"]);
        await MoveOnAndWrapUpAsync(run, _sessions.Started[^1].SessionId, "Design summary.");
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null);
        await DoneAsync(_sessions.Started[^1].SessionId, "done", "Built.");
        await DoneAsync(_sessions.Started[^1].SessionId, "pass", "Good.");
        await DoneAsync(_sessions.Started[^1].SessionId, "works", "It works.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null);

        _sessions.Started.Select(s => s.StepId).ShouldBe(["design", "plan", "implement", "review", "verify", "open-pr"]);
        Prompt("design").ShouldStartWith($"Design {Request}.\n");
        Prompt("plan").ShouldStartWith($"Plan {Request}.\n");
        foreach (var step in new[] { "implement", "review", "verify", "open-pr" })
            Prompt(step).ShouldStartWith($"The request: {Request}\n", customMessage: step);
    }

    [Fact]
    public async Task plan_carries_the_requests_constraints_and_lists_the_choices_it_made()
    {
        await StartAsync();

        var plan = Prompt("plan");
        plan.ShouldContain("Carry every constraint in the request into the plan, such as what not to install, run or change.");
        plan.ShouldContain(
            "Where the request, the design and the code disagree, choose what changes the least and keep going.\n"
            + "List each choice under a \"Decisions to confirm\" heading near the top of the plan: the user reads the plan before approving it.\n"
            + "Ask a question only if you truly can't go on without the answer.");
    }

    // ── finish: agent ──────────────────────────────────────────────────────────

    [Fact]
    public async Task a_step_whose_file_says_finish_agent_ends_with_the_tool_even_with_check_with_me_on()
    {
        var run = await StartAsync(checkWithMe: true);
        await MoveOnAndWrapUpAsync(run, _sessions.Started[^1].SessionId, "Plan.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null, checkWithMe: true);

        // Steps with no finish: follow Check with me.
        var implement = _sessions.Started[^1];
        implement.UserFinishes.ShouldBeTrue();
        await MoveOnAndWrapUpAsync(run, implement.SessionId, "Built.");
        var review = _sessions.Started[^1];
        review.UserFinishes.ShouldBeTrue();
        await MoveOnAndWrapUpAsync(run, review.SessionId, "Good.", outcome: "pass");

        // Waiting at Open the pull request: the push is still to come, and it isn't one you finish.
        var steps = _events.Last.Steps;
        (steps.Single(s => s.Id == "open-pr").FinishAgent, steps.Single(s => s.Id == "open-pr").WithYou).ShouldBe((true, false));
        steps.Single(s => s.Id == "review").FinishAgent.ShouldBeFalse();

        await _runner.AnswerAsync(UserId, run, "choice:0", null);
        var push = _sessions.Started[^1];
        push.StepId.ShouldBe("open-pr");
        push.UserFinishes.ShouldBeFalse();
        push.Prompt.ShouldEndWith(FleetWorkflows.Footer(["opened", "failed"]));
        Visit("open-pr").Finish.ShouldBe(WorkflowFinishers.Agent);
        _events.Last.WithYou.ShouldBeNull();
        _events.Last.CheckWithMe.ShouldBeTrue();

        (await DoneAsync(push.SessionId, "opened", "https://github.com/o/r/pull/12")).Accepted.ShouldBeTrue();
        _runs.Run(run).Result.ShouldBe("PR #12 opened");
    }

    [Fact]
    public async Task a_step_with_no_finish_still_follows_check_with_me_from_the_next_step()
    {
        var run = await StartAsync();
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null);
        _sessions.Started[^1].UserFinishes.ShouldBeFalse();

        await _runner.SetCheckWithMeAsync(UserId, run, on: true);
        (_events.Last.Steps.Single(s => s.Id == "review").WithYou, _events.Last.Steps.Single(s => s.Id == "open-pr").WithYou).ShouldBe((true, false));

        await DoneAsync(_sessions.Started[^1].SessionId, "done", "Built.");
        _sessions.Started[^1].StepId.ShouldBe("review");
        _sessions.Started[^1].UserFinishes.ShouldBeTrue();
    }

    // ── Committing declared files ──────────────────────────────────────────────

    [Fact]
    public async Task a_step_the_agent_finishes_has_its_declared_files_committed_after_the_check()
    {
        await StartAsync();

        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");

        Said(_files.Commits.ShouldHaveSingleItem()).ShouldBe((Worktree, PlanFile, "Plan"));
        var visit = Visit("plan");
        (visit.FilesChecked, visit.FilesCommit, visit.FilesCommitError).ShouldBe((true, "c0ffee1", null));
        var session = _events.Last.Sessions.Single(s => s.StepId == "plan");
        (session.FilesCommit, session.FilesCommitError).ShouldBe(("c0ffee1", null));
    }

    [Fact]
    public async Task a_step_you_finish_has_its_declared_files_committed_after_the_wrap_up()
    {
        var run = await StartAsync(optional: ["design"]);

        await MoveOnAndWrapUpAsync(run, _sessions.Started[^1].SessionId, "Design summary.");

        Said(_files.Commits.ShouldHaveSingleItem()).ShouldBe((Worktree, $"{DesignDoc}, {DesignMockup}", "Design"));
        Visit("design").FilesCommit.ShouldBe("c0ffee1");
        _sessions.Started[^1].StepId.ShouldBe("plan");
    }

    [Fact]
    public async Task a_step_without_declared_files_commits_nothing()
    {
        var run = await StartAsync();
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null);

        await DoneAsync(_sessions.Started[^1].SessionId, "done", "Built.");

        _files.Commits.Select(c => c.Title).ShouldBe(["Plan"]);
        Visit("implement").FilesCommit.ShouldBeNull();
    }

    [Fact]
    public async Task nothing_to_commit_records_nothing_and_the_run_goes_on()
    {
        var run = await StartAsync();
        _files.NextCommit = WorkflowFilesCommit.Nothing;

        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");

        var visit = Visit("plan");
        (visit.FilesChecked, visit.FilesCommit, visit.FilesCommitError).ShouldBe((true, null, null));
        _runs.Run(run).CurrentStepId.ShouldBe("ok-plan");
    }

    [Fact]
    public async Task a_path_git_ignores_stays_out_of_the_commit_and_the_run_goes_on()
    {
        var run = await StartAsync(optional: ["design"]);
        _files.NextCommit = new WorkflowFilesCommit("abc1234", [DesignDoc], null);

        await MoveOnAndWrapUpAsync(run, _sessions.Started[^1].SessionId, "Design summary.");

        Visit("design").FilesCommit.ShouldBe("abc1234");
        _sessions.Started[^1].StepId.ShouldBe("plan");
        _sessions.Started[^1].Prompt.ShouldContain($"Read {DesignDoc} and {DesignMockup} first.");
    }

    [Fact]
    public async Task a_commit_that_fails_is_recorded_and_doesnt_stop_the_run()
    {
        var run = await StartAsync();
        _files.NextCommit = WorkflowFilesCommit.Failed("no email was given and auto-detection is disabled");

        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");

        var visit = Visit("plan");
        (visit.FilesChecked, visit.FilesCommit, visit.FilesCommitError).ShouldBe((true, null, "no email was given and auto-detection is disabled"));
        var waiting = _runs.Run(run);
        (waiting.Status, waiting.CurrentStepId, waiting.WaitingKind).ShouldBe((WorkflowRunStatus.Waiting, "ok-plan", null));
        _events.Last.Sessions.Single(s => s.StepId == "plan").FilesCommitError.ShouldBe("no email was given and auto-detection is disabled");
        _events.Last.Waiting!.Kind.ShouldBe(WorkflowWaitingKinds.You);
    }

    [Fact]
    public async Task a_missing_file_is_committed_once_a_reply_brings_it()
    {
        var run = await StartAsync();
        _files.Absent.Add(PlanFile);
        var plan = _sessions.Started[^1];

        await DoneAsync(plan.SessionId, "ready", "Plan.");
        _files.Commits.ShouldBeEmpty();

        _files.Absent.Clear();
        await AnswerAsync(plan.SessionId, parentId: "msg_user_1");

        _files.Commits.ShouldHaveSingleItem().Files.ShouldBe([PlanFile]);
        Visit("plan").FilesCommit.ShouldBe("c0ffee1");
        _runs.Run(run).CurrentStepId.ShouldBe("ok-plan");
    }

    [Fact]
    public async Task move_on_anyway_past_a_missing_file_commits_nothing()
    {
        var run = await StartAsync();
        _files.Absent.Add(PlanFile);
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");

        await _runner.AnswerAsync(UserId, run, WorkflowRunner.MoveOnAnywayChoice, null);

        _files.Commits.ShouldBeEmpty();
        Visit("plan").FilesCommit.ShouldBeNull();
    }

    private string Prompt(string stepId) => _sessions.Started.Last(s => s.StepId == stepId).Prompt;

    private static (string? Worktree, string Files, string Title) Said(CommitCall call) => (call.Worktree, string.Join(", ", call.Files), call.Title);
}
