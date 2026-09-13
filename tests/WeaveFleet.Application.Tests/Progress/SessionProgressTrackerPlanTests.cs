using WeaveFleet.Application.Progress;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Tests.Progress;

public sealed class SessionProgressTrackerPlanTests
{
    private const string PlanPath = ".weave/plans/thin-proxy.md";
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A three-phase plan with the given steps ticked.</summary>
    private static PlanDocument Plan(params int[] ticked)
    {
        string Box(int n, string title) => $"- [{(ticked.Contains(n) ? "x" : " ")}] {n}. {title}";
        return ChecklistPlanParser.Parse(string.Join('\n',
            "# Thin Proxy Simplification",
            "### Phase 1: Proxy",
            Box(1, "Create the proxy"),
            Box(2, "Map the messages"),
            "### Phase 2: Switch",
            Box(3, "Replace the snapshot"),
            Box(4, "Replace the history"),
            "### Phase 3: Clean up",
            Box(5, "Drop the tables")));
    }

    private static SessionProgress? Apply(SessionProgress? current, PlanDocument? document, DateTimeOffset at, string? messageId = null, string path = PlanPath)
        => SessionProgressTracker.ApplyPlanFile(current, "fleet-1", "user-1", path, document, messageId, at);

    [Fact]
    public void A_checklist_with_three_steps_becomes_the_sessions_plan()
    {
        var progress = Apply(null, Plan(1, 2), T0);

        progress.ShouldNotBeNull();
        (progress.Kind, progress.Done, progress.Total, progress.Current).ShouldBe((SessionProgressKinds.Plan, 2, 5, "Replace the snapshot"));
        var plan = progress.Plans.ShouldHaveSingleItem();
        (plan.Path, plan.Title, plan.TrackedSince).ShouldBe((PlanPath, "Thin Proxy Simplification", T0));
        plan.Groups.Select(group => group.Title).ShouldBe(["Phase 1: Proxy", "Phase 2: Switch", "Phase 3: Clean up"]);

        // Boxes already ticked when Fleet first reads the file have no tick time.
        plan.Steps.Where(step => step.Checked).ShouldAllBe(step => step.TickedAt == null);
    }

    [Fact]
    public void A_file_with_fewer_than_three_steps_is_not_a_plan()
    {
        var progress = Apply(null, ChecklistPlanParser.Parse("# PR\n- [ ] Tests pass\n- [ ] Docs updated"), T0);

        progress.ShouldBeNull();
    }

    [Fact]
    public void A_newly_ticked_box_gets_the_time_and_the_message()
    {
        var first = Apply(null, Plan(1, 2), T0)!;

        var ticked = Apply(first, Plan(1, 2, 3), T0.AddMinutes(9), messageId: "msg-9");

        ticked.ShouldNotBeNull();
        (ticked.Done, ticked.Current).ShouldBe((3, "Replace the history"));
        var step = ticked.Plans[0].Steps.Single(s => s.Key == "3");
        (step.Checked, step.TickedAt, step.TickedInMessageId).ShouldBe((true, T0.AddMinutes(9), "msg-9"));
        ticked.Plans[0].LastTickedAt.ShouldBe(T0.AddMinutes(9));
        ticked.Plans[0].TrackedSince.ShouldBe(T0);
    }

    [Fact]
    public void Ticks_keep_their_time_on_later_edits()
    {
        var progress = Apply(Apply(null, Plan(), T0)!, Plan(1), T0.AddMinutes(1), "msg-1")!;

        progress = Apply(progress, Plan(1, 2), T0.AddMinutes(5), "msg-5")!;

        var steps = progress.Plans[0].Steps.ToDictionary(step => step.Key);
        (steps["1"].TickedAt, steps["1"].TickedInMessageId).ShouldBe((T0.AddMinutes(1), "msg-1"));
        (steps["2"].TickedAt, steps["2"].TickedInMessageId).ShouldBe((T0.AddMinutes(5), "msg-5"));
    }

    [Fact]
    public void Two_boxes_ticked_in_one_edit_both_get_the_time()
    {
        var progress = Apply(Apply(null, Plan(), T0)!, Plan(1, 2), T0.AddMinutes(3))!;

        progress.Plans[0].Steps.Where(step => step.Checked).ShouldAllBe(step => step.TickedAt == T0.AddMinutes(3));
        progress.Done.ShouldBe(2);
    }

    [Fact]
    public void An_unticked_box_loses_its_tick()
    {
        var progress = Apply(Apply(null, Plan(), T0)!, Plan(1, 2), T0.AddMinutes(3))!;

        progress = Apply(progress, Plan(1), T0.AddMinutes(4))!;

        progress.Done.ShouldBe(1);
        var step = progress.Plans[0].Steps.Single(s => s.Key == "2");
        (step.Checked, step.TickedAt).ShouldBe((false, (DateTimeOffset?)null));
    }

    [Fact]
    public void The_same_plan_again_changes_nothing()
    {
        var progress = Apply(null, Plan(1), T0)!;

        Apply(progress, Plan(1), T0.AddMinutes(1)).ShouldBeNull();
    }

    [Fact]
    public void A_plan_that_is_deleted_or_emptied_stops_being_tracked()
    {
        var withTodos = SessionProgressTracker.ApplyTodos(null, "fleet-1", "user-1",
            [new TodoEntry { Content = "Tidy up", Status = TodoStatuses.InProgress }], T0)!;
        var progress = Apply(withTodos, Plan(1), T0.AddMinutes(1))!;
        progress.Kind.ShouldBe(SessionProgressKinds.Plan);

        var deleted = Apply(progress, null, T0.AddMinutes(2));
        var emptied = Apply(progress, ChecklistPlanParser.Parse("# Done\nNothing left."), T0.AddMinutes(2));

        foreach (var result in new[] { deleted, emptied })
        {
            result.ShouldNotBeNull();
            result.Plans.ShouldBeEmpty();
            (result.Kind, result.Done, result.Total, result.Current).ShouldBe((SessionProgressKinds.Todos, 0, 1, "Tidy up"));
        }

        Apply(withTodos, null, T0).ShouldBeNull();
    }

    [Fact]
    public void Todos_update_but_the_counts_stay_with_the_plan()
    {
        var progress = Apply(null, Plan(1, 2), T0)!;

        progress = SessionProgressTracker.ApplyTodos(progress, "fleet-1", "user-1",
            [new TodoEntry { Content = "Write the test", Status = TodoStatuses.InProgress }], T0.AddMinutes(1))!;

        (progress.Kind, progress.Done, progress.Total, progress.Current).ShouldBe((SessionProgressKinds.Plan, 2, 5, "Replace the snapshot"));
        progress.Todos.ShouldHaveSingleItem().Content.ShouldBe("Write the test");
    }

    [Fact]
    public void The_most_recently_ticked_plan_wins_else_the_most_recently_written()
    {
        var progress = Apply(null, Plan(), T0, path: "a.md")!;
        progress = Apply(progress, Plan(), T0.AddMinutes(1), path: "b.md")!;
        SessionProgressTracker.ActivePlan(progress)!.Path.ShouldBe("b.md");

        progress = Apply(progress, Plan(1), T0.AddMinutes(2), path: "a.md")!;
        SessionProgressTracker.ActivePlan(progress)!.Path.ShouldBe("a.md");

        // Writing b again without ticking doesn't take over from the plan being ticked.
        progress = Apply(progress, ChecklistPlanParser.Parse("- [ ] 1. One\n- [ ] 2. Two\n- [ ] 3. Three\n- [ ] 4. Four"), T0.AddMinutes(3), path: "b.md")!;
        SessionProgressTracker.ActivePlan(progress)!.Path.ShouldBe("a.md");
        progress.Total.ShouldBe(5);
    }

    [Fact]
    public void A_session_remembers_at_most_five_plans()
    {
        SessionProgress? progress = null;
        for (var i = 0; i < 7; i++)
            progress = Apply(progress, Plan(), T0.AddMinutes(i), path: $"plan-{i}.md");

        progress!.Plans.Select(plan => plan.Path).ShouldBe(["plan-6.md", "plan-5.md", "plan-4.md", "plan-3.md", "plan-2.md"]);
    }

    [Fact]
    public void The_detail_carries_the_active_plan_with_its_tick_times()
    {
        var progress = Apply(Apply(null, Plan(1), T0)!, Plan(1, 2), T0.AddMinutes(9), "msg-9")!;

        var detail = SessionProgressTracker.ToDto(progress);

        var plan = detail.Plan.ShouldNotBeNull();
        (plan.Path, plan.Title, plan.TrackedSince).ShouldBe((PlanPath, "Thin Proxy Simplification", "2026-09-13T12:00:00.0000000Z"));
        var steps = plan.Groups.SelectMany(group => group.Steps).ToList();
        (steps[0].Checked, steps[0].TickedAt).ShouldBe((true, (string?)null));
        (steps[1].Checked, steps[1].TickedAt, steps[1].TickedInMessageId).ShouldBe((true, "2026-09-13T12:09:00.0000000Z", "msg-9"));
        (steps[2].Number, steps[2].Title, steps[2].Checked).ShouldBe(("3", "Replace the snapshot", false));
        SessionProgressTracker.ToSummary(progress).ShouldBe(new("fleet-1", SessionProgressKinds.Plan, 2, 5, "Replace the snapshot"));
    }
}
