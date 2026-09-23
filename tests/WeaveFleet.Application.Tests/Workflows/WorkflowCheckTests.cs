using Shouldly;
using WeaveFleet.Application.Workflows;

namespace WeaveFleet.Application.Tests.Workflows;

public sealed class WorkflowCheckTests
{
    private const string File = ".weave/workflows/deps.yaml";

    private const string WithComments = """
        # Weekly dependency bump. Kept by the platform team; ask in #platform before changing.
        name: Weekly dependency bump
        starts-from: sentence
        runs-in: new-worktree
        steps:
          - id: update
            title: Update packages
            model: standard
            # Minor versions only: majors need a person.
            prompt: |
              Update bun and NuGet packages to their latest minor versions.
              # this line is part of the prompt, not a comment
              The request: {{request}}
            outcomes: [done, nothing]
            on: { nothing: end }
          - id: test
            title: "Run the tests # all of them"
            model: github-copilot/gpt-5.4-mini   # cheap and good enough for a test run
            prompt: Run the client and server unit tests.
            outcomes: [pass, fail]
            on: { fail: update, max: 2 }
        """;

    [Fact]
    public void finds_the_comments_and_not_a_hash_inside_text()
    {
        var comments = WorkflowComments.Find(WithComments);

        comments.ShouldBe([
            new WorkflowComment(1, "Weekly dependency bump. Kept by the platform team; ask in #platform before changing."),
            new WorkflowComment(9, "Minor versions only: majors need a person."),
            new WorkflowComment(18, "cheap and good enough for a test run"),
        ]);
    }

    [Fact]
    public void a_file_without_comments_has_none()
        => WorkflowComments.Find(WorkflowCatalog.BuiltIns.Single().Text.Replace("# Fleet workflow, built into Fleet. The format is described in docs/workflows.md.\n", "")).ShouldBeEmpty();

    [Fact]
    public void a_valid_file_comes_back_with_its_draft_and_comments()
    {
        var check = WorkflowCheck.Text(WithComments, File);

        check.Errors.ShouldBeEmpty();
        check.Text.ShouldBe(WithComments);
        check.Draft.ShouldNotBeNull().Steps.Select(s => s.Id).ShouldBe(["update", "test"]);
        check.Comments.Count.ShouldBe(3);
    }

    [Fact]
    public void each_error_has_its_line_and_the_step_that_holds_it()
    {
        var text = """
            name: Deps
            steps:
              - id: update
                title: Update
                model: standard
                prompt: Update.
                outcomes: [done]
              - id: test
                title: Test
                model: standard
                prompt: Test {{requets}}.
                outcomes: [pass, fail]
                on: { fail: update }
            """;

        var check = WorkflowCheck.Text(text, File);

        check.Errors.Select(e => (e.Line, e.Step)).ShouldBe([(13, (int?)1), (11, (int?)1)], ignoreOrder: true);
        check.Errors.ShouldContain(e => e.Line == 13 && e.Message == "test sends work back to an earlier step, so it needs a max: how many times it may do that in a run.");
        check.Errors.ShouldContain(e => e.Line == 11 && e.Message.StartsWith("test's prompt uses {{requets}}.", StringComparison.Ordinal));

        // The designer can show it: a missing max and a bad variable are there to fix.
        check.Draft.ShouldNotBeNull();
    }

    [Fact]
    public void an_error_outside_any_step_has_no_step()
    {
        var check = WorkflowCheck.Text("name: Deps\nstarts-from: issue\nsteps:\n  - id: a\n    title: A\n    model: fast\n    prompt: A.\n    outcomes: [done]\n", File);

        check.Errors.ShouldHaveSingleItem().ShouldBe(new WorkflowProblem(2, "This version of Fleet can only start a workflow from a sentence, not from an issue.", null));
    }

    [Theory]
    [InlineData("name: Deps\nsteps:\n  - title: No id\n    model: fast\n    prompt: A.\n    outcomes: [done]\n  - id: b\n    title: B\n    model: fast\n    prompt: B.\n    outcomes: [done]\n")]
    [InlineData("name: Deps\nsteps:\n  - id: a\n    title: A\n    model: fast\n    colour: blue\n    prompt: A.\n    outcomes: [done]\n")]
    public void text_the_designer_would_lose_some_of_stays_in_the_file_view(string text)
    {
        var check = WorkflowCheck.Text(text, File);

        check.Errors.ShouldNotBeEmpty();
        check.Draft.ShouldBeNull();
    }

    [Fact]
    public void text_that_is_not_yaml_has_a_line_and_no_draft()
    {
        var check = WorkflowCheck.Text("name: Deps\nsteps:\n  - id: a\n   title: [broken\n", File);

        var error = check.Errors.ShouldHaveSingleItem();
        error.Line.ShouldBeGreaterThan(0);
        error.Message.ShouldStartWith("This isn't valid YAML");
        check.Draft.ShouldBeNull();
    }

    [Fact]
    public void a_draft_is_written_then_checked_and_its_errors_land_on_its_steps()
    {
        var workflow = WorkflowCatalog.BuiltIns.Single().Definition!;
        var review = (WorkflowAgentStep)workflow.Find("review")!;
        var steps = workflow.Steps.Select(s => s.Id == "review" ? review with { MaxLoops = null } : s).ToList();

        var check = WorkflowCheck.Draft(workflow with { Steps = steps }, File);

        var error = check.Errors.ShouldHaveSingleItem();
        error.Step.ShouldBe(4);
        check.Text.Split('\n')[error.Line - 1].ShouldBe("    on: { changes: implement }");
        check.Comments.ShouldBeEmpty();
    }
}
