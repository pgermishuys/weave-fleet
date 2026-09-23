using Shouldly;
using WeaveFleet.Application.Skills;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Workflows;

/// <summary>A step's skill is checked again as the step starts: it may have been turned off since the run started.</summary>
public sealed partial class WorkflowRunnerTests
{
    private async Task<string> RunToReviewWithSkillOffAsync()
    {
        _skills.Shipped.Add(new BuiltInSkill("fleet-code-review", "Reviews a branch."));
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-code-review");
        var run = await StartAsync();
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null);

        // Turned off while Implement runs.
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "");
        await DoneAsync(_sessions.Started[^1].SessionId, "done", "Built.");
        return run;
    }

    [Fact]
    public async Task a_step_whose_skill_was_turned_off_waits_instead_of_starting_without_it()
    {
        var run = await RunToReviewWithSkillOffAsync();

        _sessions.Started.Select(s => s.StepId).ShouldBe(["plan", "implement"]);
        var waiting = _runs.Run(run).ShouldSatisfy(WorkflowRunStatus.Waiting);
        waiting.CurrentStepId.ShouldBe("review");
        waiting.WaitingKind.ShouldBe(WorkflowWaitingKinds.SkillOff);
        Visit("review").Status.ShouldBe(WorkflowRunStepStatus.Waiting);

        var card = _events.Last.Waiting.ShouldNotBeNull();
        card.Kind.ShouldBe(WorkflowWaitingKinds.SkillOff);
        card.Message.ShouldBe("Review uses fleet-code-review, which is now off.");
        card.Choices.Select(c => (c.Id, c.Label)).ShouldBe([("retry", "Retry"), ("without-skill", "Start without it")]);
        card.SessionId.ShouldBe(_sessions.Started[^1].SessionId);
        _events.NeedsYou.ShouldContain(r => r.Id == run);
    }

    [Fact]
    public async Task retry_while_the_skill_is_still_off_says_so_and_keeps_waiting()
    {
        var run = await RunToReviewWithSkillOffAsync();

        var retried = await _runner.AnswerAsync(UserId, run, "retry", null);

        retried.IsFailure.ShouldBeTrue();
        retried.Error.Description.ShouldBe("fleet-code-review is still off. Turn it on in Settings → Skills, then retry.");
        _sessions.Started.Count.ShouldBe(2);
        _runs.Run(run).WaitingKind.ShouldBe(WorkflowWaitingKinds.SkillOff);
    }

    [Fact]
    public async Task retry_after_turning_the_skill_on_starts_the_step_with_it()
    {
        var run = await RunToReviewWithSkillOffAsync();
        _preferences.Seed(BuiltInSkillService.PreferenceKey, "fleet-code-review");

        (await _runner.AnswerAsync(UserId, run, "retry", null)).IsSuccess.ShouldBeTrue();

        var review = _sessions.Started[^1];
        review.StepId.ShouldBe("review");
        review.Prompt.ShouldContain("Use the fleet-code-review skill.");
        _runs.Run(run).ShouldSatisfy(WorkflowRunStatus.Running).WaitingKind.ShouldBeNull();
        Visit("review").Status.ShouldBe(WorkflowRunStepStatus.Running);
    }

    [Fact]
    public async Task start_without_it_runs_the_step_without_the_skill_for_the_rest_of_the_run()
    {
        var run = await RunToReviewWithSkillOffAsync();

        (await _runner.AnswerAsync(UserId, run, "without-skill", null)).IsSuccess.ShouldBeTrue();
        var review = _sessions.Started[^1];
        review.StepId.ShouldBe("review");
        review.Prompt.ShouldNotContain("fleet-code-review");
        WorkflowRunOptions.Read(_runs.Run(run).Options).WithoutSkills.ShouldBe(["review"]);

        // Sent back to Implement, then Review again: it doesn't ask twice.
        await DoneAsync(review.SessionId, "changes", "A bug.");
        await DoneAsync(_sessions.Started[^1].SessionId, "done", "Fixed.");
        var again = _sessions.Started[^1];
        again.StepId.ShouldBe("review");
        again.Prompt.ShouldNotContain("fleet-code-review");
        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Running);
    }

    [Fact]
    public async Task a_push_that_fails_ends_the_run_with_the_reason_not_a_pull_request()
    {
        var run = await StartAsync();
        await RunToReviewPassAsync(run);
        await _runner.AnswerAsync(UserId, run, "choice:0", null);
        var push = _sessions.Started[^1];

        push.StepId.ShouldBe("open-pr");
        push.Prompt.ShouldContain("If you can't push the branch or open the pull request, use failed and put the reason in summary.");
        push.Prompt.ShouldEndWith(FleetWorkflows.Footer(["opened", "failed"]));

        var answer = await DoneAsync(push.SessionId, "failed", "There's no remote called origin, so nothing was pushed.\nSee https://github.com/o/r/pull/3 for how it's usually done.");

        answer.Message.ShouldContain("last step");
        var done = _runs.Run(run);
        done.Status.ShouldBe(WorkflowRunStatus.Done);
        done.Result.ShouldBe("Push and open the PR failed: There's no remote called origin, so nothing was pushed.");
    }
}
