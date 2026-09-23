using Shouldly;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Tests.Workflows;

/// <summary>Steps you finish together with the agent, declared files, and Check with me.</summary>
public sealed partial class WorkflowRunnerTests
{
    private const string DesignDoc = "docs/design/press-see-every-keyboard-shortcut.md";
    private const string DesignMockup = "docs/design/press-see-every-keyboard-shortcut.html";
    private const string PlanFile = ".weave/plans/press-see-every-keyboard-shortcut.md";

    [Fact]
    public async Task a_step_you_finish_has_no_footer_and_its_agent_cant_end_it()
    {
        var run = await StartAsync(optional: ["design"]);

        var design = _sessions.Started.ShouldHaveSingleItem();
        design.UserFinishes.ShouldBeTrue();
        design.Prompt.ShouldBe(
            "Design Press ? to see every keyboard shortcut.\n"
            + $"Write it to {DesignDoc} with a mockup next to it in {DesignMockup}.\n"
            + "Don't change any code.\n\nUse the fleet-mockups skill.");
        Visit("design").Finish.ShouldBe(WorkflowFinishers.You);
        Visit("design").PromptMessageId.ShouldBe(design.PromptMessageId);

        var done = await DoneAsync(design.SessionId, "ready", "Done designing.");
        done.Accepted.ShouldBeFalse();
        done.Message.ShouldBe(WorkflowRunner.UserFinishesMessage);

        // A turn ending is just a conversation turn: no Needs you.
        await ReplyAndIdleAsync(design.SessionId);
        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Running);
        _events.NeedsYou.ShouldBeEmpty();

        var withYou = _events.Last.WithYou.ShouldNotBeNull();
        withYou.StepTitle.ShouldBe("Design");
        withYou.SessionId.ShouldBe(design.SessionId);
        withYou.Files.ShouldBe([DesignDoc, DesignMockup]);
        withYou.WrappingUp.ShouldBeFalse();
        var move = withYou.Moves.ShouldHaveSingleItem();
        (move.Outcome, move.To, move.ToTitle, move.Back, move.Allowed).ShouldBe(("ready", "plan", "Plan", false, true));
        _events.Last.Steps.Single(s => s.Id == "design").WithYou.ShouldBeTrue();
        _events.Last.Steps.Single(s => s.Id == "plan").WithYou.ShouldBeFalse();
    }

    [Fact]
    public async Task move_on_sends_one_wrap_up_and_the_reply_to_it_hands_on_the_files_the_summary_and_the_note()
    {
        var run = await StartAsync(optional: ["design"]);
        var design = _sessions.Started[^1];

        (await _runner.MoveOnAsync(UserId, run, null, "  Keep the status-bar shortcuts in the sheet. ")).IsSuccess.ShouldBeTrue();

        var wrapUp = _sessions.Prompts.ShouldHaveSingleItem();
        wrapUp.SessionId.ShouldBe(design.SessionId);
        wrapUp.Text.ShouldBe(
            $"The user is moving on to Plan. Update {DesignDoc} and {DesignMockup} with everything agreed in this conversation, "
            + "then reply with a short summary for Plan.\n\nTheir note: Keep the status-bar shortcuts in the sheet.");
        var visit = Visit("design");
        (visit.Status, visit.Outcome, visit.WrapUpMessageId, visit.HandOffNote)
            .ShouldBe((WorkflowRunStepStatus.WrappingUp, "ready", wrapUp.MessageId, "Keep the status-bar shortcuts in the sheet."));
        _events.Last.WithYou!.WrappingUp.ShouldBeTrue();

        // A turn that isn't the wrap-up's (one already running when the user pressed Move on) doesn't count.
        await AnswerAsync(design.SessionId, parentId: "msg_earlier");
        Visit("design").Status.ShouldBe(WorkflowRunStepStatus.WrappingUp);
        _sessions.Started.Count.ShouldBe(1);

        _sessions.Replies[wrapUp.MessageId] = "Summary for Plan: a ? sheet grouped Anywhere / Session / Composer.";
        await AnswerAsync(design.SessionId, parentId: wrapUp.MessageId);

        visit = Visit("design");
        (visit.Status, visit.Summary, visit.FilesChecked).ShouldBe((WorkflowRunStepStatus.Done, "Summary for Plan: a ? sheet grouped Anywhere / Session / Composer.", true));
        var plan = _sessions.Started[^1];
        plan.StepId.ShouldBe("plan");
        plan.UserFinishes.ShouldBeFalse();
        plan.Prompt.ShouldContain($"Read {DesignDoc} and {DesignMockup} first.");
        plan.Prompt.ShouldContain("Summary for Plan: a ? sheet grouped Anywhere / Session / Composer.");
        plan.Prompt.ShouldContain("Note from the user:\nKeep the status-bar shortcuts in the sheet.");
        plan.Prompt.ShouldEndWith(FleetWorkflows.Footer(["ready"]));
        _sessions.Prompts.Count.ShouldBe(1);
        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Running);
    }

    [Fact]
    public async Task a_wrap_up_answered_before_the_send_returns_still_moves_the_run_on()
    {
        var run = await StartAsync(optional: ["design"]);
        var design = _sessions.Started[^1];
        _sessions.WhileSending = sent =>
        {
            _sessions.Replies[sent.MessageId] = "Quick summary.";
            _runner.Observe(sent.SessionId, Assistant(sent.SessionId, sent.MessageId));
            _runner.Observe(sent.SessionId, Idled(sent.SessionId));
        };

        await _runner.MoveOnAsync(UserId, run, null, null);
        await _runner.Pending;

        Visit("design").Summary.ShouldBe("Quick summary.");
        _sessions.Started[^1].StepId.ShouldBe("plan");
        _sessions.Prompts.ShouldHaveSingleItem().Text.ShouldBe(
            $"The user is moving on to Plan. Update {DesignDoc} and {DesignMockup} with everything agreed in this conversation, then reply with a short summary for Plan.");
        design.SessionId.ShouldNotBe(_sessions.Started[^1].SessionId);
    }

    [Fact]
    public async Task without_design_plan_has_no_line_for_files_that_arent_there()
    {
        await StartAsync();

        var plan = _sessions.Started.ShouldHaveSingleItem();
        plan.Prompt.Contains("Read ", StringComparison.Ordinal).ShouldBeFalse();
        plan.Prompt.ShouldNotContain("first.");
        plan.Prompt.ShouldContain("Reuse what the code already has.\nWhere the request, the design and the code disagree");
        plan.Prompt.ShouldContain("can't go on without the answer.\n\n" + FleetWorkflows.Footer(["ready"]));
    }

    [Fact]
    public async Task a_missing_file_waits_and_a_reply_that_writes_it_moves_the_run_on()
    {
        var run = await StartAsync(optional: ["design"]);
        var design = _sessions.Started[^1];
        _files.Absent.Add(DesignMockup);

        await MoveOnAndWrapUpAsync(run, design.SessionId, "Summary.");

        var waiting = _runs.Run(run);
        waiting.Status.ShouldBe(WorkflowRunStatus.Waiting);
        waiting.WaitingKind.ShouldBe(WorkflowWaitingKinds.MissingFiles);
        waiting.WaitingReason.ShouldBe($"Design declares {DesignMockup}, but it isn't in the run's worktree. Reply to the agent in its session, or move on anyway.");
        var card = _events.Last.Waiting.ShouldNotBeNull();
        card.Kind.ShouldBe(WorkflowWaitingKinds.MissingFiles);
        card.SessionId.ShouldBe(design.SessionId);
        card.Files.ShouldBe([DesignDoc, DesignMockup]);
        card.Choices.Select(c => (c.Id, c.Label)).ShouldBe([(WorkflowRunner.MoveOnAnywayChoice, "Move on anyway")]);
        _events.NeedsYou.Count.ShouldBe(1);
        _sessions.Started.Count.ShouldBe(1);
        Visit("design").Summary.ShouldBe("Summary.");

        // The user asks for the file; the turn ends without it: still waiting, and no second notification.
        await AnswerAsync(design.SessionId, parentId: "msg_user_1");
        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Waiting);
        _events.NeedsYou.Count.ShouldBe(1);

        // Then with it.
        _files.Absent.Clear();
        await AnswerAsync(design.SessionId, parentId: "msg_user_2");

        _sessions.Started[^1].StepId.ShouldBe("plan");
        _sessions.Started[^1].Prompt.ShouldContain("Summary.");
        Visit("design").FilesChecked.ShouldBeTrue();
    }

    [Fact]
    public async Task the_last_events_of_the_wrap_up_dont_count_as_a_reply()
    {
        var run = await StartAsync(optional: ["design"]);
        var design = _sessions.Started[^1];
        _files.Absent.Add(DesignMockup);
        await MoveOnAndWrapUpAsync(run, design.SessionId, "Summary.");
        _files.Absent.Clear();

        await AnswerAsync(design.SessionId, parentId: _sessions.Prompts[^1].MessageId);

        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Waiting);
        _sessions.Started.Count.ShouldBe(1);
    }

    [Fact]
    public async Task move_on_anyway_goes_past_a_missing_file()
    {
        var run = await StartAsync(optional: ["design"]);
        _files.Absent.Add(DesignDoc);
        await MoveOnAndWrapUpAsync(run, _sessions.Started[^1].SessionId, "Summary.");

        (await _runner.AnswerAsync(UserId, run, "outcome:ready", null)).IsFailure.ShouldBeTrue();
        (await _runner.AnswerAsync(UserId, run, WorkflowRunner.MoveOnAnywayChoice, null)).IsSuccess.ShouldBeTrue();

        _sessions.Started[^1].StepId.ShouldBe("plan");
        Visit("design").FilesChecked.ShouldBeFalse();
        _runs.Run(run).WaitingKind.ShouldBeNull();
    }

    [Fact]
    public async Task a_step_the_agent_finishes_waits_when_its_declared_file_is_missing()
    {
        var run = await StartAsync();
        _files.Absent.Add(PlanFile);

        (await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.")).Accepted.ShouldBeTrue();

        var waiting = _runs.Run(run);
        (waiting.Status, waiting.CurrentStepId, waiting.WaitingKind).ShouldBe((WorkflowRunStatus.Waiting, "plan", WorkflowWaitingKinds.MissingFiles));
        waiting.WaitingReason.ShouldBe($"Plan declares {PlanFile}, but it isn't in the run's worktree. Reply to the agent in its session, or move on anyway.");

        await _runner.AnswerAsync(UserId, run, WorkflowRunner.MoveOnAnywayChoice, null);
        _runs.Run(run).CurrentStepId.ShouldBe("ok-plan");
    }

    [Fact]
    public async Task a_failed_wrap_up_waits_and_a_reply_gives_the_step_back_to_the_user()
    {
        var run = await StartAsync(optional: ["design"]);
        var design = _sessions.Started[^1];
        await _runner.MoveOnAsync(UserId, run, null, "A note.");
        var wrapUp = _sessions.Prompts[^1];

        _runner.Observe(design.SessionId, Assistant(design.SessionId, wrapUp.MessageId, id: "reply_1"));
        _runner.Observe(design.SessionId, new TurnFailed
        {
            Payload = new TurnFailedPayload
            {
                SessionId = design.SessionId,
                MessageId = "reply_1",
                Error = new TurnError { Name = "APIError", Message = "The provider is overloaded." },
            },
        });
        _runner.Observe(design.SessionId, Idled(design.SessionId));
        await _runner.Pending;

        var waiting = _runs.Run(run);
        (waiting.Status, waiting.WaitingKind).ShouldBe((WorkflowRunStatus.Waiting, WorkflowWaitingKinds.WrapUpFailed));
        waiting.WaitingReason.ShouldBe("Design's wrap-up failed: The provider is overloaded. Reply to the agent in its session, or move on anyway.");
        _events.NeedsYou.ShouldContain(r => r.Id == run);
        _sessions.Started.Count.ShouldBe(1);

        // The user replies; when that turn ends the step is theirs again, and Move on sends a fresh wrap-up.
        await AnswerAsync(design.SessionId, parentId: "msg_user_1");
        var open = _runs.Run(run);
        (open.Status, open.WaitingKind).ShouldBe((WorkflowRunStatus.Running, null));
        Visit("design").Status.ShouldBe(WorkflowRunStepStatus.Running);
        _events.Last.WithYou.ShouldNotBeNull().WrappingUp.ShouldBeFalse();

        await MoveOnAndWrapUpAsync(run, design.SessionId, "Summary, second go.");
        _sessions.Prompts.Count.ShouldBe(2);
        _sessions.Started[^1].StepId.ShouldBe("plan");
    }

    [Fact]
    public async Task move_on_anyway_after_a_failed_wrap_up_goes_on_without_a_summary()
    {
        var run = await StartAsync(optional: ["design"]);
        var design = _sessions.Started[^1];
        await _runner.MoveOnAsync(UserId, run, null, "Keep it small.");
        _runner.Observe(design.SessionId, Assistant(design.SessionId, _sessions.Prompts[^1].MessageId));
        _runner.Observe(design.SessionId, new TurnFailed
        {
            Payload = new TurnFailedPayload { SessionId = design.SessionId, Error = new TurnError { Name = "APIError", Message = "Boom." } },
        });
        _runner.Observe(design.SessionId, Idled(design.SessionId));
        await _runner.Pending;

        await _runner.AnswerAsync(UserId, run, WorkflowRunner.MoveOnAnywayChoice, null);

        Visit("design").Summary.ShouldBeNull();
        var plan = _sessions.Started[^1];
        plan.StepId.ShouldBe("plan");
        plan.Prompt.ShouldContain("Note from the user:\nKeep it small.");
    }

    [Fact]
    public async Task a_restart_during_a_wrap_up_waits_on_the_user()
    {
        var run = await StartAsync(optional: ["design"]);
        var design = _sessions.Started[^1];
        await _runner.MoveOnAsync(UserId, run, null, null);

        _runner = NewRunner();
        await _runner.RecoverAsync();

        var waiting = _runs.Run(run);
        (waiting.Status, waiting.WaitingKind).ShouldBe((WorkflowRunStatus.Waiting, WorkflowWaitingKinds.WrapUpFailed));
        waiting.WaitingReason.ShouldBe("Fleet restarted during Design's wrap-up, which stopped it. Reply to the agent in its session, or move on anyway.");
        _sessions.Prompts.Count.ShouldBe(1);

        // Its watch came back with the run: a reply gives the step back to the user.
        await AnswerAsync(design.SessionId, parentId: "msg_user_1");
        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Running);
    }

    [Fact]
    public async Task a_restart_leaves_a_step_you_finish_open()
    {
        var run = await StartAsync(optional: ["design"]);

        _runner = NewRunner();
        await _runner.RecoverAsync();

        _runs.Run(run).Status.ShouldBe(WorkflowRunStatus.Running);
        Visit("design").Status.ShouldBe(WorkflowRunStepStatus.Running);
        _events.NeedsYou.ShouldBeEmpty();
        (await _runner.MoveOnAsync(UserId, run, null, null)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task move_on_needs_a_step_you_finish_and_one_of_its_outcomes()
    {
        var run = await StartAsync();

        var notYours = await _runner.MoveOnAsync(UserId, run, null, null);
        notYours.Error.Description.ShouldBe("There's no step open for you to move on from.");

        var withYou = await StartAsync(optional: ["design"]);
        var wrong = await _runner.MoveOnAsync(UserId, withYou, "approved", null);
        wrong.Error.Description.ShouldBe("Pick how Design went: ready.");
    }

    [Fact]
    public async Task check_with_me_from_the_run_box_makes_every_step_one_you_finish()
    {
        await StartAsync(checkWithMe: true);

        var plan = _sessions.Started.ShouldHaveSingleItem();
        plan.UserFinishes.ShouldBeTrue();
        plan.Prompt.ShouldNotContain("fleet_step_done");
        _events.Last.CheckWithMe.ShouldBeTrue();
    }

    [Fact]
    public async Task the_approval_offers_two_ways_to_approve_and_opens_the_plan()
    {
        var run = await StartAsync();
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");

        var card = _events.Last.Waiting.ShouldNotBeNull();
        card.Files.ShouldBe([PlanFile]);
        card.Choices.Select(c => (c.Label, c.Involvement)).ShouldBe([("Approve", true), ("Send back with a note", false)]);
        card.ThenAlone.ShouldBe(["Implement", "Review"]);
        card.NextYouTitle.ShouldBe("Open the pull request");

        (await _runner.AnswerAsync(UserId, run, "choice:0", null, checkWithMe: true)).IsSuccess.ShouldBeTrue();

        var implement = _sessions.Started[^1];
        implement.StepId.ShouldBe("implement");
        implement.UserFinishes.ShouldBeTrue();
        WorkflowRunOptions.Read(_runs.Run(run).Options).CheckWithMe.ShouldBeTrue();
        _events.Last.Steps.Where(s => s.Kind == "agent" && s.Enabled && s.State == "pending" && !s.FinishAgent).ShouldAllBe(s => s.WithYou);

        // Its file says finish: agent, so Check with me doesn't make the push one you finish.
        var push = _events.Last.Steps.Single(s => s.Id == "open-pr");
        (push.FinishAgent, push.WithYou).ShouldBe((true, false));
    }

    [Fact]
    public async Task approve_let_it_run_turns_check_with_me_off()
    {
        var run = await StartAsync(checkWithMe: true);
        await MoveOnAndWrapUpAsync(run, _sessions.Started[^1].SessionId, "Plan.");

        await _runner.AnswerAsync(UserId, run, "choice:0", null, checkWithMe: false);

        _sessions.Started[^1].UserFinishes.ShouldBeFalse();
        _events.Last.CheckWithMe.ShouldBeFalse();
    }

    [Fact]
    public async Task the_pull_request_question_has_one_way_to_answer()
    {
        var run = await StartAsync();
        await RunToReviewPassAsync(run);

        var card = _events.Last.Waiting.ShouldNotBeNull();
        card.StepId.ShouldBe("ok-pr");
        card.Choices.ShouldAllBe(c => !c.Involvement);
        card.ThenAlone.ShouldBeEmpty();
    }

    [Fact]
    public async Task check_with_me_from_the_header_applies_from_the_next_step()
    {
        var run = await StartAsync();
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null);
        var implement = _sessions.Started[^1];
        implement.UserFinishes.ShouldBeFalse();

        // Switched on while Implement runs: Implement still ends when its agent says so.
        (await _runner.SetCheckWithMeAsync(UserId, run, on: true)).Value.CheckWithMe.ShouldBeTrue();
        _events.Last.Steps.Single(s => s.Id == "implement").WithYou.ShouldBeFalse();
        _events.Last.Steps.Single(s => s.Id == "review").WithYou.ShouldBeTrue();
        (await DoneAsync(implement.SessionId, "done", "Built.")).Accepted.ShouldBeTrue();

        var review = _sessions.Started[^1];
        review.StepId.ShouldBe("review");
        review.UserFinishes.ShouldBeTrue();

        // Switched off while Review is with the user: Review is still theirs to finish.
        await _runner.SetCheckWithMeAsync(UserId, run, on: false);
        (await DoneAsync(review.SessionId, "pass", "Good.")).Message.ShouldBe(WorkflowRunner.UserFinishesMessage);
        _events.Last.WithYou.ShouldNotBeNull().StepId.ShouldBe("review");

        await MoveOnAndWrapUpAsync(run, review.SessionId, "No bugs.", outcome: "pass");
        _runs.Run(run).CurrentStepId.ShouldBe("ok-pr");
    }

    [Fact]
    public async Task review_with_you_goes_where_you_pick_and_its_loop_maximum_still_counts()
    {
        var run = await StartAsync();
        await DoneAsync(_sessions.Started[^1].SessionId, "ready", "Plan.");
        await _runner.AnswerAsync(UserId, run, "choice:0", null, checkWithMe: true);

        for (var pass = 1; pass <= 2; pass++)
        {
            await MoveOnAndWrapUpAsync(run, _sessions.Started[^1].SessionId, $"Built, pass {pass}.");
            var review = _sessions.Started[^1];
            review.StepId.ShouldBe("review");
            review.Prompt.ShouldContain($"Built, pass {pass}.");

            var moves = _events.Last.WithYou.ShouldNotBeNull().Moves;
            moves.Select(m => (m.Outcome, m.ToTitle, m.Back, m.LoopsUsed, m.MaxLoops, m.Allowed))
                .ShouldBe([("pass", "Open the pull request", false, (int?)null, (int?)null, true), ("changes", "Implement", true, pass - 1, 2, true)]);

            await MoveOnAndWrapUpAsync(run, review.SessionId, $"Fix {pass}.", outcome: "changes");
            _sessions.Prompts[^1].Text.ShouldBe("The user chose changes and is moving on to Implement. Reply with a short summary for Implement.");
            _sessions.Started[^1].StepId.ShouldBe("implement");
            _sessions.Started[^1].Prompt.ShouldContain($"Fix {pass}.");
        }

        await MoveOnAndWrapUpAsync(run, _sessions.Started[^1].SessionId, "Built, pass 3.");
        var third = _events.Last.WithYou.ShouldNotBeNull().Moves.Single(m => m.Outcome == "changes");
        (third.LoopsUsed, third.Allowed).ShouldBe(((int?)2, false));

        var refused = await _runner.MoveOnAsync(UserId, run, "changes", null);
        refused.Error.Description.ShouldBe("Review has sent the work back to Implement twice, the most this run allows. Pick another outcome, or end the run.");

        await MoveOnAndWrapUpAsync(run, _sessions.Started[^1].SessionId, "No bugs.", outcome: "pass");
        _sessions.Prompts[^1].Text.ShouldBe("The user chose pass and is moving on to Open the pull request. Reply with a short summary for Open the pull request.");
        _runs.Run(run).CurrentStepId.ShouldBe("ok-pr");
    }

    [Fact]
    public void the_wrap_up_prompt_names_the_outcome_the_files_and_the_note()
    {
        WorkflowRunner.WrapUpPrompt("Plan", null, ["a.md"], null)
            .ShouldBe("The user is moving on to Plan. Update a.md with everything agreed in this conversation, then reply with a short summary for Plan.");
        WorkflowRunner.WrapUpPrompt("Implement", "changes", [], "Reuse the store's order.")
            .ShouldBe("The user chose changes and is moving on to Implement. Reply with a short summary for Implement.\n\nTheir note: Reuse the store's order.");
        WorkflowRunner.WrapUpPrompt(null, null, ["a.md", "b.md", "c.md"], null)
            .ShouldBe("The user is finishing the run here. Update a.md, b.md and c.md with everything agreed in this conversation, then reply with a short summary of where things stand.");
    }

    private async Task MoveOnAndWrapUpAsync(string run, string sessionId, string reply, string? outcome = null)
    {
        (await _runner.MoveOnAsync(UserId, run, outcome, null)).IsSuccess.ShouldBeTrue();
        var wrapUp = _sessions.Prompts[^1];
        wrapUp.SessionId.ShouldBe(sessionId);
        _sessions.Replies[wrapUp.MessageId] = reply;
        await AnswerAsync(sessionId, parentId: wrapUp.MessageId);
    }

    /// <summary>An assistant message answering <paramref name="parentId"/>, then the session going idle.</summary>
    private async Task AnswerAsync(string sessionId, string parentId)
    {
        _runner.Observe(sessionId, Assistant(sessionId, parentId));
        _runner.Observe(sessionId, Idled(sessionId));
        await _runner.Pending;
    }

    private static MessageUpdated Assistant(string sessionId, string parentId, string? id = null) => new()
    {
        Payload = new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = id ?? $"reply_{Guid.NewGuid():N}",
                Role = "assistant",
                SessionId = sessionId,
                ParentId = parentId,
                Time = new MessageEventTime { Created = 0 },
            },
        },
    };
}
