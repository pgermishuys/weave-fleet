using WeaveFleet.Application.Progress;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Tests.Progress;

public sealed class SessionProgressTrackerSubagentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private static SessionProgress PlanAt(params int[] ticked)
    {
        string Box(int n) => $"- [{(ticked.Contains(n) ? "x" : " ")}] {n}. Step {n}";
        return SessionProgressTracker.ApplyPlanFile(
            null, "parent", "user-1", "plan.md",
            ChecklistPlanParser.Parse(string.Join('\n', "# Plan", Box(1), Box(2), Box(3))),
            messageId: null, T0)!;
    }

    private static SessionProgress? Delegate(SessionProgress? current, string delegationId, string status, string? child = null, DateTimeOffset? at = null)
        => SessionProgressTracker.ApplyDelegation(current, "parent", "user-1", delegationId, child, "shuttle", status, at ?? T0.AddMinutes(1));

    [Fact]
    public void A_new_subagent_is_tied_to_the_plans_current_step()
    {
        var progress = Delegate(PlanAt(1), "del-1", "pending");

        var subagent = progress!.Subagents.ShouldHaveSingleItem();
        (subagent.DelegationId, subagent.Agent, subagent.Status, subagent.StepKey, subagent.StartedAt)
            .ShouldBe(("del-1", "shuttle", "pending", "2", T0.AddMinutes(1)));
        subagent.ChildSessionId.ShouldBeNull();

        // The parent's own counts don't change.
        (progress.Kind, progress.Done, progress.Total).ShouldBe((SessionProgressKinds.Plan, 1, 3));
    }

    [Fact]
    public void A_subagent_keeps_its_step_as_the_plan_moves_on()
    {
        var progress = Delegate(PlanAt(1), "del-1", "pending")!;
        progress = SessionProgressTracker.ApplyPlanFile(progress, "parent", "user-1", "plan.md",
            ChecklistPlanParser.Parse("# Plan\n- [x] 1. Step 1\n- [x] 2. Step 2\n- [ ] 3. Step 3"), null, T0.AddMinutes(5))!;

        progress = Delegate(progress, "del-2", "pending", at: T0.AddMinutes(6))!;

        progress.Subagents.Select(s => (s.DelegationId, s.StepKey)).ShouldBe([("del-1", "2"), ("del-2", "3")]);
    }

    [Fact]
    public void Linking_and_finishing_update_the_same_subagent()
    {
        var progress = Delegate(PlanAt(), "del-1", "pending")!;

        progress = Delegate(progress, "del-1", "running", child: "child-1", at: T0.AddMinutes(2))!;
        progress.Subagents.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.ChildSessionId.ShouldBe("child-1"),
            s => s.Status.ShouldBe("running"),
            s => s.StartedAt.ShouldBe(T0.AddMinutes(1)));

        progress = Delegate(progress, "del-1", "completed", at: T0.AddMinutes(9))!;
        progress.Subagents.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.ChildSessionId.ShouldBe("child-1"),
            s => s.Status.ShouldBe("completed"));

        Delegate(progress, "del-1", "completed").ShouldBeNull();
    }

    [Fact]
    public void Without_a_plan_a_subagent_has_no_step()
    {
        var progress = Delegate(null, "del-1", "pending");

        progress!.Subagents.ShouldHaveSingleItem().StepKey.ShouldBeNull();
        (progress.Kind, progress.Total).ShouldBe((SessionProgressKinds.Todos, 0));
    }

    [Fact]
    public void A_session_remembers_at_most_twenty_subagents()
    {
        SessionProgress? progress = null;
        for (var i = 0; i < 25; i++)
            progress = Delegate(progress, $"del-{i}", "pending");

        progress!.Subagents.Count.ShouldBe(SessionProgressTracker.MaxSubagents);
        progress.Subagents[0].DelegationId.ShouldBe("del-5");
    }

    [Fact]
    public void A_subagent_sessions_progress_is_copied_onto_its_entry()
    {
        var parent = Delegate(Delegate(PlanAt(1), "del-1", "pending")!, "del-1", "running", child: "child-1")!;
        var child = SessionProgressTracker.ApplyTodos(null, "child-1", "user-1",
        [
            new TodoEntry { Content = "Delete SnapshotMergeTests.cs", Status = TodoStatuses.Completed },
            new TodoEntry { Content = "Update HarnessEventRelayTests.cs", Status = TodoStatuses.InProgress },
        ], T0.AddMinutes(3))!;

        var updated = SessionProgressTracker.ApplySubagentProgress(parent, "child-1", child, "Delete or update affected tests", T0.AddMinutes(3));

        updated.ShouldNotBeNull();
        updated.Subagents.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => (s.Done, s.Total, s.Current).ShouldBe((1, 2, "Update HarnessEventRelayTests.cs")),
            s => s.Title.ShouldBe("Delete or update affected tests"));
        (updated.Done, updated.Total).ShouldBe((1, 3));

        SessionProgressTracker.ApplySubagentProgress(updated, "child-1", child, "Delete or update affected tests", T0.AddMinutes(4)).ShouldBeNull();
        SessionProgressTracker.ApplySubagentProgress(updated, "someone-else", child, null, T0.AddMinutes(4)).ShouldBeNull();
    }

    [Fact]
    public void The_task_description_names_the_subagent_over_its_session_title()
    {
        var parent = SessionProgressTracker.ApplyDelegation(PlanAt(), "parent", "user-1", "del-1", null, "shuttle", "pending", T0, "Delete or update affected tests")!;
        parent = Delegate(parent, "del-1", "running", child: "child-1")!;

        var updated = SessionProgressTracker.ApplySubagentProgress(parent, "child-1", null, "shuttle", T0.AddMinutes(1));

        parent.Subagents.ShouldHaveSingleItem().Title.ShouldBe("Delete or update affected tests");
        updated.ShouldBeNull();
    }

    [Fact]
    public void The_detail_lists_subagents_with_their_steps()
    {
        var parent = Delegate(Delegate(PlanAt(1), "del-1", "pending")!, "del-1", "running", child: "child-1")!;

        var detail = SessionProgressTracker.ToDto(parent);

        detail.Subagents.ShouldHaveSingleItem().ShouldBe(new("del-1", "child-1", "shuttle", null, "running", "2", 0, 0, null));
    }
}
