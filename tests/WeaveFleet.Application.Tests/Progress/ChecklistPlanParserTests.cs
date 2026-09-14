using WeaveFleet.Application.Progress;

namespace WeaveFleet.Application.Tests.Progress;

public sealed class ChecklistPlanParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Progress", "Fixtures", name));

    [Fact]
    public void A_phased_weave_plan_reads_as_groups_of_numbered_steps()
    {
        var plan = ChecklistPlanParser.Parse(Fixture("thin-proxy-simplification.md"));

        plan.Title.ShouldBe("Thin Proxy Simplification");
        plan.Groups.Select(group => (group.Title, group.Steps.Count)).ShouldBe(
        [
            ("Phase 1: Add opencode proxy infrastructure", 2),
            ("Phase 2: Switch to proxy", 3),
            ("Phase 3: Remove buffering from fan-out", 3),
            ("Phase 4: Delete dead code and tables", 4),
            ("Phase 5: Test cleanup", 2),
            ("Considerations", 3),
        ]);
        plan.Steps.Count().ShouldBe(17);
        plan.Steps.Select(step => step.Number).ShouldBe(Enumerable.Range(1, 17).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        plan.Steps.ShouldAllBe(step => step.Checked);

        var first = plan.Steps.First();
        first.Title.ShouldBe("Create `ISessionMessageProxy` interface and opencode implementation");
        first.Key.ShouldBe("1");
        // Paths only: `ISessionMessageProxy` in the title is a type name, not a file.
        first.Mentions.ShouldBe(
        [
            "src/WeaveFleet.Application/Services/ISessionMessageProxy.cs",
            "src/WeaveFleet.Infrastructure/Services/OpenCodeSessionMessageProxy.cs",
            "src/WeaveFleet.Infrastructure/DependencyInjection.cs",
        ], ignoreOrder: true);
    }

    [Fact]
    public void Code_fences_are_skipped()
    {
        // The fixture's Verification section has "# Backend compiles" inside a fence; it must not become a heading.
        var plan = ChecklistPlanParser.Parse(Fixture("thin-proxy-simplification.md"));

        plan.Groups.ShouldNotContain(group => group.Title == "Backend compiles");
        plan.Groups[^1].Title.ShouldBe("Considerations");
    }

    [Fact]
    public void A_flat_plan_is_one_group()
    {
        var plan = ChecklistPlanParser.Parse(Fixture("session-tags.md"));

        plan.Title.ShouldBe("Session & Automation Tags");
        var group = plan.Groups.ShouldHaveSingleItem();
        group.Title.ShouldBe("Tasks");
        group.Steps.Count.ShouldBe(13);
        group.Steps[9].Title.ShouldBe("Add PATCH /sessions/{id}/tags endpoint");
    }

    [Fact]
    public void A_superpowers_style_plan_counts_its_checkbox_steps_under_each_task()
    {
        var plan = ChecklistPlanParser.Parse("""
            # Beta Tester Harness Implementation Plan

            ### Task 1: Promote the test harness

            **Files:**
            - Modify: `src/WeaveFleet.Api/Program.cs`

            - [x] **Step 1: Write the failing test**
            - [ ] **Step 2: Run it to see it fail**

            ### Task 2: Scenario playbooks

            - [ ] **Step 1: Write the first playbook**
            """);

        plan.Groups.Select(group => (group.Title, group.Steps.Count)).ShouldBe(
        [
            ("Task 1: Promote the test harness", 2),
            ("Task 2: Scenario playbooks", 1),
        ]);
        plan.Steps.Select(step => step.Checked).ShouldBe([true, false, false]);
        plan.Steps.First().Title.ShouldBe("**Step 1: Write the failing test**");
    }

    [Fact]
    public void Indented_checkboxes_are_sub_steps()
    {
        var plan = ChecklistPlanParser.Parse("""
            ## Work
            - [ ] Build the parser
              - [x] Headings
              - [x] Fences
                - [ ] Tildes
            - [x] Ship it
            """);

        var steps = plan.Steps.ToList();
        steps.Count.ShouldBe(2);
        (steps[0].SubDone, steps[0].SubTotal).ShouldBe((2, 3));
        (steps[1].SubDone, steps[1].SubTotal).ShouldBe((0, 0));
    }

    [Theory]
    [InlineData("- [x] Done", true)]
    [InlineData("- [X] Done", true)]
    [InlineData("* [x] Done", true)]
    [InlineData("+ [ ] Open", false)]
    [InlineData("1. [ ] Open", false)]
    [InlineData("2) [x] Done", true)]
    public void Every_checkbox_marker_is_read(string line, bool isChecked)
    {
        var step = ChecklistPlanParser.Parse(line).Steps.ShouldHaveSingleItem();

        step.Checked.ShouldBe(isChecked);
    }

    [Fact]
    public void Windows_line_endings_are_read_the_same()
    {
        var plan = ChecklistPlanParser.Parse("# Plan\r\n## Phase 1\r\n- [x] 1. One\r\n- [ ] 2. Two\r\n");

        plan.Title.ShouldBe("Plan");
        plan.Steps.Select(step => (step.Number, step.Title, step.Checked)).ShouldBe([("1", "One", true), ("2", "Two", false)]);
    }

    [Fact]
    public void A_file_without_checkboxes_has_no_steps()
    {
        var plan = ChecklistPlanParser.Parse("""
            # Notes
            - a plain list
            - [link](https://example.com)

            ```
            - [ ] inside a fence
            ```
            """);

        plan.Groups.ShouldBeEmpty();
        plan.Steps.ShouldBeEmpty();
    }

    [Fact]
    public void Steps_before_any_heading_have_no_group_title()
    {
        var plan = ChecklistPlanParser.Parse("- [ ] One\n- [ ] Two\n# Later\n- [ ] Three");

        plan.Groups.Select(group => (group.Title, group.Steps.Count)).ShouldBe([(null, 2), ("Later", 1)]);
    }

    [Fact]
    public void A_step_without_a_number_is_keyed_by_its_title()
    {
        var step = ChecklistPlanParser.Parse("- [ ] Write the docs").Steps.ShouldHaveSingleItem();

        step.Number.ShouldBeNull();
        step.Key.ShouldBe("Write the docs");
        step.Line.ShouldBe(1);
    }
}
