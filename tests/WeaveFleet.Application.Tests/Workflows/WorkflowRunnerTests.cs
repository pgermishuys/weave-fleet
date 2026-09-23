using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Workflows;

public sealed class WorkflowRunnerTests
{
    private const string UserId = "u1";

    private readonly InMemoryWorkflowRunRepository _runs = new();
    private readonly FakeStepSessions _sessions = new();
    private readonly FakeRunEvents _events = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly SessionActivityTracker _activity = new();
    private WorkflowRunner _runner;

    public WorkflowRunnerTests()
    {
        _preferences.Seed(FleetWorkflows.PreferenceKey, "true");
        _runner = NewRunner();
    }

    [Fact]
    public async Task build_a_feature_runs_from_plan_to_the_pull_request()
    {
        var run = await StartAsync();

        // Design is optional and off, so the run starts at Plan.
        var plan = _sessions.Started.ShouldHaveSingleItem();
        plan.StepId.ShouldBe("plan");
        plan.Prompt.ShouldStartWith("Plan Press ? to see every keyboard shortcut. Write .weave/plans/press-see-every-keyboard-shortcut.md");
        plan.Prompt.ShouldEndWith(FleetWorkflows.Footer(["ready"]));
        Visit("design").Status.ShouldBe(WorkflowRunStepStatus.Skipped);

        await DoneAsync(plan.SessionId, "ready", "The plan has four steps. Tests: a unit test and an E2E check.");
        var waiting = _runs.Run(run).ShouldSatisfy(WorkflowRunStatus.Waiting);
        waiting.CurrentStepId.ShouldBe("ok-plan");
        var card = _events.Last.Waiting.ShouldNotBeNull();
        card.Kind.ShouldBe(WorkflowWaitingKinds.You);
        card.SessionId.ShouldBe(plan.SessionId);
        card.Question.ShouldBe("Build it this way?");
        card.Choices.Select(c => c.Label).ShouldBe(["Approve", "Send back with a note"]);
        _events.NeedsYou.ShouldContain(r => r.Id == run);

        (await _runner.AnswerAsync(UserId, run, "choice:0", null)).IsSuccess.ShouldBeTrue();
        var implement = _sessions.Started[^1];
        implement.StepId.ShouldBe("implement");
        // The hand-off is the plan's full summary, not a cut.
        implement.Prompt.ShouldContain("The plan has four steps. Tests: a unit test and an E2E check.");

        await DoneAsync(implement.SessionId, "done", "Built it.");
        var review = _sessions.Started[^1];
        review.StepId.ShouldBe("review");
        review.Prompt.ShouldContain("Use the fleet-code-review skill.");
        review.Prompt.ShouldEndWith(FleetWorkflows.Footer(["pass", "changes"]));

        await DoneAsync(review.SessionId, "changes", "The E2E check the plan named is missing.");
        var implementAgain = _sessions.Started[^1];
        implementAgain.StepId.ShouldBe("implement");
        implementAgain.Prompt.ShouldContain("The E2E check the plan named is missing.");
        _runs.Steps.Count(s => s.StepId == "implement").ShouldBe(2);
        _runs.Steps.Last(s => s.StepId == "implement").Visit.ShouldBe(2);

        await DoneAsync(implementAgain.SessionId, "done", "Added it.");
        var reviewAgain = _sessions.Started[^1];
        await DoneAsync(reviewAgain.SessionId, "pass", "Looks right. Nit: a long line in ShortcutSheet.vue.");

        // Check it runs is optional and off: straight to the pull-request question.
        Visit("verify").Status.ShouldBe(WorkflowRunStepStatus.Skipped);
        _runs.Run(run).CurrentStepId.ShouldBe("ok-pr");
        _events.Last.Waiting!.SessionId.ShouldBe(reviewAgain.SessionId);

        await _runner.AnswerAsync(UserId, run, "choice:0", null);
        var pr = _sessions.Started[^1];
        pr.StepId.ShouldBe("open-pr");
        pr.Prompt.ShouldContain("Looks right. Nit: a long line in ShortcutSheet.vue.");
        pr.Model.ShouldBe(new WorkflowModelChoice("fake/fast", null));

        var answer = await DoneAsync(pr.SessionId, "opened", "https://github.com/o/r/pull/12");
        answer.Message.ShouldContain("last step");
        var done = _runs.Run(run);
        done.Status.ShouldBe(WorkflowRunStatus.Done);
        done.Result.ShouldBe("PR #12 opened");
    }

    [Fact]
    public async Task keep_the_branch_only_ends_the_run()
    {
        var run = await StartAsync();
        await RunToReviewPassAsync(run);

        await _runner.AnswerAsync(UserId, run, "choice:1", null);

        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Done);
        _sessions.Started.ShouldNotContain(s => s.StepId == "open-pr");
    }

    [Fact]
    public async Task sending_the_plan_back_puts_the_note_into_its_next_prompt()
    {
        var run = await StartAsync();
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan v1.");

        var noNote = await _runner.AnswerAsync(UserId, run, "choice:1", "  ");
        noNote.IsFailure.ShouldBeTrue();
        noNote.Error.Description.ShouldBe("Write what should change first.");

        (await _runner.AnswerAsync(UserId, run, "choice:1", "Leave the status bar alone.")).IsSuccess.ShouldBeTrue();

        var plan = _sessions.Started[^1];
        plan.StepId.ShouldBe("plan");
        plan.Prompt.ShouldContain("Sent back with a note:\nLeave the status bar alone.");
        plan.Prompt.ShouldEndWith(FleetWorkflows.Footer(["ready"]));
        _runs.Steps.Last(s => s.StepId == "plan").Visit.ShouldBe(2);
        _runs.Steps.Single(s => s.StepId == "ok-plan").Status.ShouldBe(WorkflowRunStepStatus.Decided);
    }

    [Fact]
    public async Task a_third_send_back_from_review_goes_to_the_user()
    {
        var run = await StartAsync();
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null);

        for (var i = 0; i < 2; i++)
        {
            await DoneAsync(_sessions.Started[^1].SessionId, "done", "Built.");
            await DoneAsync(_sessions.Started[^1].SessionId, "changes", $"Fix {i}.");
            _sessions.Started[^1].StepId.ShouldBe("implement");
        }

        await DoneAsync(_sessions.Started[^1].SessionId, "done", "Built.");
        var review = _sessions.Started[^1];
        var third = await DoneAsync(review.SessionId, "changes", "Fix 3.");
        third.Accepted.ShouldBeTrue();

        var waiting = _runs.Run(run);
        waiting.Status.ShouldBe(WorkflowRunStatus.Waiting);
        waiting.WaitingReason.ShouldBe("Review sent the work back to Implement 3 times; it may do that at most twice. Pick where it goes next, or end the run.");
        var card = _events.Last.Waiting.ShouldNotBeNull();
        card.Kind.ShouldBe(WorkflowWaitingKinds.LoopLimit);
        card.SessionId.ShouldBe(review.SessionId);
        _sessions.Started[^1].ShouldBe(review);

        // The user can still pick it; they decided, so the count doesn't stop them.
        await _runner.AnswerAsync(UserId, run, "outcome:pass", null);
        _runs.Run(run).CurrentStepId.ShouldBe("ok-pr");
    }

    [Fact]
    public async Task an_optional_step_runs_when_switched_on()
    {
        await StartAsync(optional: ["design"]);

        var design = _sessions.Started.ShouldHaveSingleItem();
        design.StepId.ShouldBe("design");
        design.Prompt.ShouldContain("Use the fleet-mockups skill.");
        design.Model.ShouldBe(new WorkflowModelChoice("fake/strong", "high"));
    }

    [Fact]
    public async Task a_step_that_goes_idle_without_the_tool_waits_on_the_user()
    {
        var run = await StartAsync();
        var plan = _sessions.Started[^1];

        await ReplyAndIdleAsync(plan.SessionId);

        var waiting = _runs.Run(run);
        waiting.Status.ShouldBe(WorkflowRunStatus.Waiting);
        waiting.WaitingReason.ShouldBe("Plan stopped without finishing the step. Reply to the agent to carry on, or pick an outcome.");
        var card = _events.Last.Waiting.ShouldNotBeNull();
        card.Kind.ShouldBe(WorkflowWaitingKinds.NoOutcome);
        card.SessionId.ShouldBe(plan.SessionId);
        card.Choices.Select(c => c.Id).ShouldBe(["outcome:ready"]);
        _sessions.Started.Count.ShouldBe(1);
        _events.NeedsYou.ShouldContain(r => r.Id == run);

        // The user replies to the agent, which then finishes the step.
        (await DoneAsync(plan.SessionId, "ready", "Plan.")).Accepted.ShouldBeTrue();
        _runs.Run(run).CurrentStepId.ShouldBe("ok-plan");
    }

    [Fact]
    public async Task picking_an_outcome_for_a_stopped_step_moves_the_run_on()
    {
        var run = await StartAsync();
        await ReplyAndIdleAsync(_sessions.Started[^1].SessionId);

        await _runner.AnswerAsync(UserId, run, "outcome:ready", null);

        _runs.Run(run).CurrentStepId.ShouldBe("ok-plan");
    }

    [Fact]
    public async Task idle_before_the_steps_turn_started_is_not_a_stop()
    {
        var run = await StartAsync();

        _runner.Observe(_sessions.Started[^1].SessionId, Idled(_sessions.Started[^1].SessionId));
        await _runner.Pending;

        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Running);
    }

    [Fact]
    public async Task a_step_still_in_its_turn_after_idle_is_not_stopped()
    {
        var run = await StartAsync();
        var session = _sessions.Started[^1].SessionId;
        _activity.Update(session, "busy", UserId);

        await ReplyAndIdleAsync(session);

        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Running);
    }

    [Fact]
    public async Task a_restart_picks_the_run_up_where_it_was()
    {
        var running = await StartAsync();
        var finished = await StartAsync();
        var finishedPlan = _sessions.Started[^1].SessionId;

        // The plan of the second run finished just before the restart, but its next step wasn't reached.
        var visit = _runs.Steps.Single(s => s.SessionId == finishedPlan);
        visit.Status = WorkflowRunStepStatus.Done;
        visit.Outcome = "ready";
        visit.Summary = "Plan.";
        await _runs.UpdateStepAsync(visit);

        _runner = NewRunner();
        await _runner.RecoverAsync();

        // The first run's step lost its turn: it waits on the user, and nothing is prompted.
        var waiting = _runs.Run(running);
        waiting.Status.ShouldBe(WorkflowRunStatus.Waiting);
        waiting.WaitingReason.ShouldStartWith("Fleet restarted while Plan was running");
        _sessions.Started.Count.ShouldBe(2);

        // The second carries on to the approval.
        _runs.Run(finished).CurrentStepId.ShouldBe("ok-plan");
        _runs.Run(finished).Status.ShouldBe(WorkflowRunStatus.Waiting);

        // And the first can still finish if the agent is replied to after the restart.
        (await DoneAsync(_sessions.Started[0].SessionId, "ready", "Plan.")).Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task an_outcome_the_step_doesnt_have_is_refused_with_the_ones_it_has()
    {
        await StartAsync();

        var result = await DoneAsync(_sessions.Started[^1].SessionId, "approved", "…");

        result.Accepted.ShouldBeFalse();
        result.Message.ShouldBe("\"approved\" isn't an outcome of this step. Call fleet_step_done again with outcome set to one of: ready.");
    }

    [Fact]
    public async Task a_step_finishes_once()
    {
        await StartAsync();
        var plan = _sessions.Started[^1].SessionId;
        await DoneAsync(plan, "ready", "Plan.");

        var again = await DoneAsync(plan, "ready", "Plan again.");

        again.Accepted.ShouldBeFalse();
        again.Message.ShouldBe("This step is already done. Stop here.");
    }

    [Fact]
    public async Task a_session_that_isnt_a_step_cant_finish_one()
    {
        await StartAsync();

        var result = await DoneAsync("some-other-session", "ready", "…");

        result.Accepted.ShouldBeFalse();
        result.Message.ShouldBe(WorkflowStepBridge.NotAStepMessage);
    }

    [Fact]
    public async Task ending_a_run_stops_it_advancing_and_keeps_its_sessions()
    {
        var run = await StartAsync();
        var plan = _sessions.Started[^1].SessionId;

        var ended = await _runner.EndAsync(UserId, run);

        ended.Value.Status.ShouldBe(WorkflowRunStatus.Ended);
        ended.Value.Result.ShouldBe("Ended by you at Plan");
        var after = await DoneAsync(plan, "ready", "Plan.");
        after.Accepted.ShouldBeFalse();
        _sessions.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_step_fleet_couldnt_start_can_be_tried_again()
    {
        _sessions.FailNext = "OpenCode isn't installed.";
        var run = await StartAsync();

        var waiting = _runs.Run(run);
        waiting.Status.ShouldBe(WorkflowRunStatus.Waiting);
        waiting.WaitingReason.ShouldBe("Fleet couldn't start Plan: OpenCode isn't installed.");
        _events.Last.Waiting!.Kind.ShouldBe(WorkflowWaitingKinds.StartFailed);

        (await _runner.AnswerAsync(UserId, run, "retry", null)).IsSuccess.ShouldBeTrue();

        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Running);
        _sessions.Started.ShouldHaveSingleItem().StepId.ShouldBe("plan");
    }

    [Fact]
    public async Task the_first_step_makes_the_worktree_and_later_steps_use_it()
    {
        var run = await StartAsync();

        var saved = _runs.Run(run);
        saved.WorktreePath.ShouldBe("/repo-worktrees/press-see-every-keyboard-shortcut");
        saved.Branch.ShouldBe("fleet/press-see-every-keyboard-shortcut");
    }

    private async Task<string> StartAsync(IReadOnlyList<string>? optional = null)
    {
        var entry = WorkflowCatalog.BuiltIns.Single(e => e.Id == "builtin:build-a-feature");
        var workflow = entry.Definition!;
        var roles = new Dictionary<string, WorkflowModelChoice>
        {
            [WorkflowRoles.Strong] = new("fake/strong", "high"),
            [WorkflowRoles.Standard] = new("fake/standard", null),
            [WorkflowRoles.Fast] = new("fake/fast", null),
        };
        var models = workflow.Steps.OfType<WorkflowAgentStep>()
            .ToDictionary(step => step.Id, step => WorkflowModelRoles.Resolve(step, roles, overrides: null));
        var run = new WorkflowRun
        {
            Id = Ulid.NewUlid().ToString(),
            UserId = UserId,
            WorkflowId = entry.Id,
            WorkflowName = workflow.Name,
            Definition = entry.Text,
            Request = "Press ? to see every keyboard shortcut",
            Slug = "press-see-every-keyboard-shortcut",
            Title = "Press ? to see every keyboard shortcut",
            RepositoryPath = "/repo",
            BaseBranch = "main",
            HarnessType = "opencode",
            Options = new WorkflowRunOptions { OptionalSteps = [.. optional ?? []], StepModels = models }.Write(),
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
        };

        await _runner.StartAsync(run);
        return run.Id;
    }

    private async Task RunToReviewPassAsync(string run)
    {
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null);
        await DoneAsync(_sessions.Started[^1].SessionId, "done", "Built.");
        await DoneAsync(_sessions.Started[^1].SessionId, "pass", "Good.");
    }

    private async Task<WorkflowStepDoneResult> DoneAsync(string sessionId, string outcome, string summary)
    {
        var result = await _runner.StepDoneAsync(UserId, sessionId, outcome, summary);
        await _runner.Pending;
        return result;
    }

    private async Task ReplyAndIdleAsync(string sessionId)
    {
        _runner.Observe(sessionId, new MessageUpdated
        {
            Payload = new MessageLifecyclePayload
            {
                Info = new MessageEventInfo
                {
                    Id = $"reply_{Guid.NewGuid():N}",
                    Role = "assistant",
                    SessionId = sessionId,
                    Time = new MessageEventTime { Created = 0 },
                },
            },
        });
        _runner.Observe(sessionId, Idled(sessionId));
        await _runner.Pending;
    }

    private static SessionIdled Idled(string sessionId) => new() { Payload = new SessionIdledPayload { SessionId = sessionId } };

    private WorkflowRunStep Visit(string stepId) => _runs.Steps.Last(s => s.StepId == stepId);

    private WorkflowRunner NewRunner() => new(
        TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IWorkflowRunRepository>(_runs);
            services.AddSingleton<IWorkflowStepSessions>(_sessions);
            services.AddSingleton<IWorkflowRunEvents>(_events);
            services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
            services.AddSingleton(new FleetOptions());
            services.AddSingleton<IUserPreferenceRepository>(_preferences);
            services.AddScoped<WorkflowsFeature>();
        }),
        _activity,
        TimeProvider.System,
        NullLogger<WorkflowRunner>.Instance)
    {
        IdleGrace = TimeSpan.Zero,
    };

    internal sealed record StartedStep(string StepId, string SessionId, string Prompt, WorkflowModelChoice Model);

    private sealed class FakeStepSessions : IWorkflowStepSessions
    {
        private int _next;

        public List<StartedStep> Started { get; } = [];
        public string? FailNext { get; set; }

        public Task<WorkflowStepSession> StartAsync(WorkflowRun run, WorkflowAgentStep agentStep, string prompt, WorkflowModelChoice model, CancellationToken ct)
        {
            if (FailNext is { } error)
            {
                FailNext = null;
                return Task.FromResult(WorkflowStepSession.Failed(error));
            }

            var id = $"s{Interlocked.Increment(ref _next)}";
            Started.Add(new StartedStep(agentStep.Id, id, prompt, model));
            return Task.FromResult(run.WorktreePath is null
                ? new WorkflowStepSession(id, $"/repo-worktrees/{run.Slug}", $"fleet/{run.Slug}", null)
                : new WorkflowStepSession(id, run.WorktreePath, run.Branch, null));
        }
    }

    private sealed class FakeRunEvents : IWorkflowRunEvents
    {
        private readonly Lock _gate = new();
        private readonly List<WorkflowRunDto> _changed = [];

        public List<WorkflowRunDto> NeedsYou { get; } = [];

        public WorkflowRunDto Last
        {
            get
            {
                lock (_gate)
                    return _changed[^1];
            }
        }

        public Task ChangedAsync(WorkflowRunDto run, string userId)
        {
            lock (_gate)
                _changed.Add(run);
            return Task.CompletedTask;
        }

        public Task NeedsYouAsync(WorkflowRunDto run, string userId)
        {
            lock (_gate)
                NeedsYou.Add(run);
            return Task.CompletedTask;
        }
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

internal static class WorkflowRunAssertions
{
    public static WorkflowRun ShouldSatisfy(this WorkflowRun run, string status)
    {
        run.Status.ShouldBe(status);
        return run;
    }
}
