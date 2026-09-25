using Shouldly;
using WeaveFleet.Application.Workflows;

namespace WeaveFleet.Application.Tests.Workflows;

public sealed class WorkflowYamlWriterTests
{
    private const string File = ".weave/workflows/x.yaml";

    public static TheoryData<string> Examples() => [.. AllExamples()];

    private static List<string> AllExamples()
    {
        var data = WorkflowCatalog.BuiltIns.Select(entry => entry.Text).ToList();

        // Every whole workflow in the docs: a fenced yaml block that starts with its name.
        var docs = System.IO.File.ReadAllText(Path.Combine(RepoRoot(), "docs", "workflows.md"));
        var parts = docs.Split("```yaml\n");
        foreach (var part in parts.Skip(1))
        {
            var block = part[..part.IndexOf("```", StringComparison.Ordinal)];
            if (block.StartsWith("name:", StringComparison.Ordinal))
                data.Add(block);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void what_it_writes_reads_back_as_the_same_workflow(string text)
    {
        var original = WorkflowYaml.Parse(text, File);
        original.Errors.ShouldBeEmpty();
        var workflow = original.Definition!;

        var written = WorkflowYamlWriter.Write(workflow);
        var again = WorkflowYaml.Parse(written, File);

        again.Errors.ShouldBeEmpty(written);
        Same(again.Definition!, workflow);

        // And writing it again changes nothing.
        WorkflowYamlWriter.Write(again.Definition!).ShouldBe(written);
    }

    [Fact]
    public void the_docs_have_a_whole_workflow_to_round_trip()
        => AllExamples().Count.ShouldBeGreaterThan(WorkflowCatalog.BuiltIns.Count);

    [Fact]
    public void it_writes_the_built_in_in_fleets_layout()
    {
        var written = WorkflowYamlWriter.Write(WorkflowCatalog.BuiltIns.Single(e => e.Id == "builtin:build-a-feature").Definition!);

        written.ShouldStartWith("""
            name: Build a feature
            description: >-
              Turns a sentence into a reviewed pull request. It stops once, for you to approve the plan, before
            """.Replace("\r\n", "\n"));
        written.ShouldContain("""
              - id: design
                title: Design
                agent: build
                model: strong
                skill: fleet-mockups
                optional: For UI and new features
                finish: you
                writes:
                  - docs/design/{{slug}}.md
                  - docs/design/{{slug}}.html
                prompt: |
                  Design {{request}}.
            """.Replace("\r\n", "\n"));
        written.ShouldContain("""
                outcomes: [pass, changes]
                on: { changes: implement, max: 2 }

              - id: verify
            """.Replace("\r\n", "\n"));
        written.ShouldContain("""
                choices:
                  Approve: implement
                  Send back with a note: { to: plan, note: true }
            """.Replace("\r\n", "\n"));
        written.ShouldNotContain("#");
    }

    [Theory]
    [InlineData("Plain words")]
    [InlineData("Build it: now")]
    [InlineData("Is it # a comment")]
    [InlineData("#starts with a hash")]
    [InlineData("- looks like a list")]
    [InlineData("\"quoted\" and 'single'")]
    [InlineData("true")]
    [InlineData("No")]
    [InlineData("42")]
    [InlineData("1e3")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("ends with a colon:")]
    [InlineData(" leading space")]
    [InlineData("trailing space ")]
    [InlineData("tab\there")]
    [InlineData("back\\slash")]
    [InlineData("{ brace } [ bracket ], comma")]
    [InlineData("Ünïcödé — and 漢字 and 🚀")]
    [InlineData("@at and `tick`")]
    [InlineData("a\u0007bell")]
    public void awkward_text_reads_back_as_it_was(string text)
    {
        var step = new WorkflowAgentStep(
            "work", text.Trim() is { Length: > 0 } title ? title : "t", 0, "build", "standard", null, null, true,
            bool.TryParse(text.Trim(), out _) ? null : text.Trim(),
            text + "\n" + text + "\n", ["done"], new Dictionary<string, string>(), null, null, []);
        var you = new WorkflowYouStep("ask", "Ask", 0, text.Trim(), [new WorkflowChoice(text.Trim(), "end", false), new WorkflowChoice("Other", "work", true)]);
        var workflow = new WorkflowDefinition(text.Trim(), text.Trim(), text.Trim(), "sentence", "new-worktree", [step, you]);

        var written = WorkflowYamlWriter.Write(workflow);
        var again = WorkflowYaml.Parse(written, File);

        // A title may be "true" or "42"; what matters is that it comes back as written.
        again.Draft.ShouldNotBeNull(written);
        Same(again.Draft!, workflow);
    }

    [Theory]
    [InlineData("one line")]
    [InlineData("one line\n")]
    [InlineData("two\nlines\n")]
    [InlineData("kept\n\n\n")]
    [InlineData("  indented first line\nthen not\n")]
    [InlineData("a blank\n\nin the middle\n")]
    [InlineData("  ")]
    [InlineData("")]
    [InlineData("windows\r\nline\r\n")]
    public void a_prompt_reads_back_exactly(string prompt)
    {
        var step = new WorkflowAgentStep("work", "Work", 0, null, "standard", null, null, false, null, prompt, ["done"], new Dictionary<string, string>(), null, null, []);
        var workflow = new WorkflowDefinition("W", null, null, "sentence", "new-worktree", [step]);

        var written = WorkflowYamlWriter.Write(workflow);
        var again = WorkflowYaml.Parse(written, File).Draft.ShouldNotBeNull(written);

        again.Steps[0].ShouldBeOfType<WorkflowAgentStep>().Prompt.ShouldBe(prompt, written);
    }

    [Fact]
    public void a_long_description_folds_and_reads_back_the_same()
    {
        var description = string.Join(' ', Enumerable.Repeat("A sentence about what it does, with a colon: and a # in it.", 6));
        var workflow = new WorkflowDefinition("W", description, null, "sentence", "new-worktree", []);

        var written = WorkflowYamlWriter.Write(workflow);

        written.ShouldContain("description: >-\n");
        written.Split('\n').ShouldAllBe(line => line.Length <= 104);
        WorkflowYaml.Parse(written, File).Draft!.Description.ShouldBe(description);
    }

    [Fact]
    public void an_invalid_draft_is_written_as_it_is_so_the_parser_can_say_what_is_wrong()
    {
        var step = new WorkflowAgentStep("review", "Review", 0, null, "strong", null, null, false, null, "Review it.\n",
            ["pass", "changes"], new Dictionary<string, string> { ["changes"] = "review" }, null, null, []);
        var workflow = new WorkflowDefinition("W", null, null, "sentence", "new-worktree", [step]);

        var parsed = WorkflowYaml.Parse(WorkflowYamlWriter.Write(workflow), File);

        parsed.Errors.Select(e => e.Message).ShouldBe(["review sends work back to an earlier step, so it needs a max: how many times it may do that in a run."]);
        Same(parsed.Draft!, workflow);
    }

    private static readonly string[] NoWords = [];
    private static readonly Dictionary<string, string> NoRoutes = [];
    private static readonly WorkflowChoice[] NoChoices = [];

    /// <summary>Everything but the lines, which the writer decides.</summary>
    internal static void Same(WorkflowDefinition actual, WorkflowDefinition expected)
    {
        actual.Name.ShouldBe(expected.Name);
        actual.Description.ShouldBe(expected.Description);
        actual.Placeholder.ShouldBe(expected.Placeholder);
        actual.StartsFrom.ShouldBe(expected.StartsFrom);
        actual.RunsIn.ShouldBe(expected.RunsIn);
        actual.Steps.Count.ShouldBe(expected.Steps.Count);
        for (var i = 0; i < expected.Steps.Count; i++)
        {
            switch (expected.Steps[i])
            {
                case WorkflowAgentStep agent:
                {
                    var got = actual.Steps[i].ShouldBeOfType<WorkflowAgentStep>();
                    (got with { Line = 0, Outcomes = NoWords, Routes = NoRoutes, Writes = NoWords })
                        .ShouldBe(agent with { Line = 0, Outcomes = NoWords, Routes = NoRoutes, Writes = NoWords }, agent.Id);
                    got.Outcomes.ShouldBe(agent.Outcomes);
                    got.Routes.OrderBy(p => p.Key).ShouldBe(agent.Routes.OrderBy(p => p.Key));
                    got.Writes.ShouldBe(agent.Writes);
                    break;
                }

                case WorkflowYouStep you:
                {
                    var got = actual.Steps[i].ShouldBeOfType<WorkflowYouStep>();
                    (got with { Line = 0, Choices = NoChoices }).ShouldBe(you with { Line = 0, Choices = NoChoices });
                    got.Choices.ShouldBe(you.Choices);
                    break;
                }
            }
        }
    }

    internal static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !System.IO.File.Exists(Path.Combine(folder.FullName, "WeaveFleet.slnx")))
            folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("Can't find the repository root.");
    }
}
