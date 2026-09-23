using Shouldly;
using WeaveFleet.Application.Workflows;

namespace WeaveFleet.Application.Tests.Workflows;

public sealed class WorkflowYamlTests
{
    private const string File = ".weave/workflows/deps.yaml";

    [Fact]
    public void the_built_in_build_a_feature_reads_as_the_mockup_shows_it()
    {
        var entry = WorkflowCatalog.BuiltIns.Single(e => e.Id == "builtin:build-a-feature");

        entry.Errors.ShouldBeEmpty();
        var workflow = entry.Definition.ShouldNotBeNull();
        workflow.Name.ShouldBe("Build a feature");
        workflow.Steps.Select(s => s.Id).ShouldBe(["design", "plan", "ok-plan", "implement", "review", "verify", "ok-pr", "open-pr"]);

        var design = workflow.Find("design").ShouldBeOfType<WorkflowAgentStep>();
        design.Optional.ShouldBeTrue();
        design.OptionalHint.ShouldBe("For UI and new features");
        design.Model.ShouldBe(WorkflowRoles.Strong);
        design.Skill.ShouldBe("fleet-mockups");

        workflow.Find("plan").ShouldBeOfType<WorkflowAgentStep>().Agent.ShouldBe("plan");

        var approve = workflow.Find("ok-plan").ShouldBeOfType<WorkflowYouStep>();
        approve.Title.ShouldBe("Approve the plan");
        approve.Choices.ShouldBe([new WorkflowChoice("Approve", "implement", false), new WorkflowChoice("Send back with a note", "plan", true)]);

        workflow.Find("implement").ShouldBeOfType<WorkflowAgentStep>().Model.ShouldBe(WorkflowRoles.Standard);

        var review = workflow.Find("review").ShouldBeOfType<WorkflowAgentStep>();
        review.Outcomes.ShouldBe(["pass", "changes"]);
        review.Routes["changes"].ShouldBe("implement");
        review.MaxLoops.ShouldBe(2);
        review.Prompt.ShouldContain("Use changes only for bugs, or for tests the plan named that are missing.");

        var verify = workflow.Find("verify").ShouldBeOfType<WorkflowAgentStep>();
        verify.Optional.ShouldBeTrue();
        verify.Routes["broken"].ShouldBe("implement");
        verify.MaxLoops.ShouldBe(2);

        workflow.Find("ok-pr").ShouldBeOfType<WorkflowYouStep>().Choices.Select(c => c.To).ShouldBe(["open-pr", "end"]);
        workflow.Find("open-pr").ShouldBeOfType<WorkflowAgentStep>().Model.ShouldBe(WorkflowRoles.Fast);
    }

    [Fact]
    public void reads_a_hand_written_workflow_with_a_pinned_model()
    {
        var result = WorkflowYaml.Parse(
            """
            name: Weekly dependency bump
            steps:
              - id: bump
                title: Bump
                model: github-copilot/gpt-5.4-mini
                effort: low
                prompt: Bump the dependencies in {{request}}.
                outcomes: [done]
            """,
            File);

        result.Errors.ShouldBeEmpty();
        var step = result.Definition!.Steps.Single().ShouldBeOfType<WorkflowAgentStep>();
        step.Model.ShouldBe("github-copilot/gpt-5.4-mini");
        step.Effort.ShouldBe("low");
        step.Agent.ShouldBeNull();
        step.Optional.ShouldBeFalse();
        result.Definition.StartsFrom.ShouldBe(WorkflowStarts.Sentence);
        result.Definition.RunsIn.ShouldBe(WorkflowPlaces.NewWorktree);
    }

    [Fact]
    public void a_loop_without_a_max_names_the_file_and_line()
    {
        var result = Parse(
            """
              - id: implement
                title: Implement
                model: standard
                prompt: Build it.
                outcomes: [done]
              - id: review
                title: Review
                model: strong
                prompt: Review it.
                outcomes: [pass, changes]
                on: { changes: implement }
            """);

        var error = result.Errors.ShouldHaveSingleItem();
        error.ToString().ShouldBe($"{File}, line 15: review sends work back to an earlier step, so it needs a max: how many times it may do that in a run.");
        result.Definition.ShouldBeNull();
    }

    [Fact]
    public void a_target_that_isnt_a_step_is_an_error()
    {
        var result = Parse(
            """
              - id: review
                title: Review
                model: strong
                prompt: Review it.
                outcomes: [pass, changes]
                on: { changes: implment, max: 2 }
            """);

        result.Errors.Select(e => e.Message).ShouldContain("review: \"implment\" isn't a step in this workflow.");
    }

    [Fact]
    public void an_outcome_in_on_must_be_one_of_the_steps_outcomes()
    {
        var result = Parse(
            """
              - id: review
                title: Review
                model: strong
                prompt: Review it.
                outcomes: [pass]
                on: { changes: review, max: 2 }
            """);

        result.Errors.Select(e => e.Message).ShouldContain("review: \"changes\" isn't one of its outcomes (pass).");
    }

    [Fact]
    public void unknown_variables_and_keys_are_errors()
    {
        var result = Parse(
            """
              - id: fix
                title: Fix
                model: standard
                prompt: Fix {{issue}}.
                outcomes: [done]
                colour: blue
            """);

        result.Errors.Select(e => e.Message).ShouldContain("\"colour\" isn't something an agent step (fix) can have.");
        result.Errors.Select(e => e.Message).ShouldContain(m => m.StartsWith("fix's prompt uses {{issue}}."));
    }

    [Fact]
    public void a_model_that_is_neither_a_role_nor_provider_and_model_is_an_error()
    {
        var result = Parse(
            """
              - id: fix
                title: Fix
                model: opus
                prompt: Fix it.
                outcomes: [done]
            """);

        result.Errors.Single().Message.ShouldBe("fix's model is \"opus\": use strong, standard, fast, or an exact provider/model.");
    }

    [Fact]
    public void parallel_and_wait_steps_are_not_in_this_version()
    {
        var result = Parse(
            """
              - id: look
                parallel:
                  - id: a
            """);

        result.Errors.Select(e => e.Message).ShouldContain("Parallel steps aren't in this version of Fleet.");
    }

    [Fact]
    public void starting_from_an_issue_is_not_in_this_version()
    {
        var result = WorkflowYaml.Parse(
            """
            name: Bug triage
            starts-from: issue
            steps:
              - id: read
                title: Read
                model: fast
                prompt: Read it.
                outcomes: [clear]
            """,
            File);

        result.Errors.Single().ToString().ShouldBe($"{File}, line 2: This version of Fleet can only start a workflow from a sentence, not from an issue.");
    }

    [Fact]
    public void broken_yaml_says_where()
    {
        var result = WorkflowYaml.Parse("name: x\nsteps:\n  - id: a\n   title: [unclosed\n", File);

        var error = result.Errors.ShouldHaveSingleItem();
        error.Line.ShouldBeGreaterThan(0);
        error.Message.ShouldStartWith("This isn't valid YAML:");
    }

    [Fact]
    public void fill_replaces_variables_and_drops_the_blank_lines_an_empty_one_leaves()
    {
        var filled = WorkflowYaml.Fill(
            "Build .weave/plans/{{slug}}.md.\nRun the tests.\n\n{{previous.summary}}\n",
            variable => variable == "slug" ? "shortcut-sheet" : null);

        filled.ShouldBe("Build .weave/plans/shortcut-sheet.md.\nRun the tests.");
    }

    [Fact]
    public void steps_summary_variables_must_name_a_step()
    {
        var result = Parse(
            """
              - id: pr
                title: PR
                model: fast
                prompt: "Open it. {{steps.review.summary}}"
                outcomes: [opened]
            """);

        result.Errors.Single().Message.ShouldBe("pr's prompt uses {{steps.review.summary}}, but there's no step \"review\".");
    }

    private static WorkflowParseResult Parse(string steps)
        => WorkflowYaml.Parse($"name: Test\nstarts-from: sentence\nruns-in: new-worktree\nsteps:\n{steps}", File);
}
