using System.Text.Json;
using Shouldly;
using WeaveFleet.Domain.Events;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>Claude Code's tool calls under the names and inputs Fleet shows OpenCode's (<see cref="ClaudeCodeTools"/>).</summary>
public sealed class ClaudeCodeToolsTests
{
    [Theory]
    [InlineData("Bash", "bash")]
    [InlineData("Read", "read")]
    [InlineData("Write", "write")]
    [InlineData("Edit", "edit")]
    [InlineData("MultiEdit", "edit")]
    [InlineData("NotebookEdit", "edit")]
    [InlineData("Glob", "glob")]
    [InlineData("Grep", "grep")]
    [InlineData("WebFetch", "webfetch")]
    [InlineData("WebSearch", "websearch")]
    // Claude Code 2.1.290 calls its subagent tool Task; other builds Agent.
    [InlineData("Agent", "task")]
    [InlineData("Task", "task")]
    [InlineData("Skill", "skill")]
    [InlineData("TodoWrite", "todowrite")]
    [InlineData("AskUserQuestion", "question")]
    // Tools Fleet has no name for keep Claude Code's.
    [InlineData("Monitor", "Monitor")]
    [InlineData("TaskCreate", "TaskCreate")]
    [InlineData("mcp__github__get_issue", "mcp__github__get_issue")]
    public void A_tool_is_shown_under_Fleets_name(string claude, string fleet)
        => ClaudeCodeTools.Name(claude).ShouldBe(fleet);

    [Theory]
    [InlineData("Read", """{"file_path":"/w/a.cs","offset":10,"limit":5}""", """{"filePath":"/w/a.cs","offset":10,"limit":5}""")]
    [InlineData("Edit", """{"file_path":"/w/a.cs","old_string":"a","new_string":"b","replace_all":true}""", """{"filePath":"/w/a.cs","oldString":"a","newString":"b","replaceAll":true}""")]
    [InlineData("Grep", """{"pattern":"def ","glob":"*.py","output_mode":"content"}""", """{"pattern":"def ","include":"*.py","output_mode":"content"}""")]
    [InlineData("Skill", """{"skill":"fleet-run","args":"x"}""", """{"name":"fleet-run","args":"x"}""")]
    // A subagent call keeps subagent_type: Fleet reads it as the agent's kind, as OpenCode sends it.
    [InlineData("Agent", """{"description":"Review","prompt":"p","subagent_type":"Explore"}""", """{"description":"Review","prompt":"p","subagent_type":"Explore"}""")]
    [InlineData("NotebookEdit", """{"notebook_path":"/w/n.ipynb","new_source":"x=1","cell_id":"c1"}""", """{"filePath":"/w/n.ipynb","newSource":"x=1","cell_id":"c1"}""")]
    [InlineData("MultiEdit", """{"file_path":"/w/a.cs","edits":[{"old_string":"a","new_string":"b"}]}""", """{"filePath":"/w/a.cs","edits":[{"oldString":"a","newString":"b"}]}""")]
    [InlineData("AskUserQuestion", """{"questions":[{"question":"Q?","header":"H","options":[],"multiSelect":true}]}""", """{"questions":[{"question":"Q?","header":"H","options":[],"multiple":true}]}""")]
    [InlineData("Monitor", """{"command":"tail -f x","timeout_ms":5}""", """{"command":"tail -f x","timeout_ms":5}""")]
    public void A_tools_input_gets_Fleets_field_names(string claude, string input, string expected)
        => ClaudeCodeTools.Input(claude, Json(input)).GetRawText().ShouldBe(expected);

    [Fact]
    public void A_multi_edit_shows_a_hunk_per_edit_until_Claude_reports_its_patch()
    {
        var input = Json("""{"file_path":"/w/a.py","edits":[{"old_string":"a + b","new_string":"b + a"},{"old_string":"x\ny","new_string":"z"}]}""");

        ClaudeCodeTools.Diff("MultiEdit", input).ShouldBe(
            "--- /w/a.py\n+++ /w/a.py\n@@ -1,1 +1,1 @@\n-a + b\n+b + a\n@@ -1,2 +1,1 @@\n-x\n-y\n+z\n");

        var reported = Json("""{"structuredPatch":[{"oldStart":3,"oldLines":2,"newStart":3,"newLines":2,"lines":[" def f():","-  a + b","+  b + a"]}]}""");
        ClaudeCodeTools.Diff("MultiEdit", input, reported).ShouldBe("--- /w/a.py\n+++ /w/a.py\n@@ -3,2 +3,2 @@\n def f():\n-  a + b\n+  b + a\n");
    }

    [Fact]
    public void Only_tools_that_change_a_file_have_a_diff()
    {
        ClaudeCodeTools.Diff("Bash", Json("""{"command":"ls"}""")).ShouldBeNull();
        ClaudeCodeTools.Diff("Read", Json("""{"file_path":"/w/a.py"}""")).ShouldBeNull();
        ClaudeCodeTools.Diff("Write", Json("""{"file_path":"/w/n.txt","content":"a\nb\n"}""")).ShouldBe("--- /w/n.txt\n+++ /w/n.txt\n@@ -0,0 +1,2 @@\n+a\n+b\n");
    }

    [Theory]
    [InlineData("Edit", """{"file_path":"/w/a.py","old_string":"a","new_string":"b"}""", """{"filePath":"/w/a.py"}""", "/w/a.py")]
    [InlineData("Write", """{"file_path":"/w/n.txt","content":"x"}""", """{"type":"create","filePath":"/w/n.txt"}""", "/w/n.txt")]
    // No result to read it from: the path the call asked for, against the session's folder when it's relative.
    [InlineData("MultiEdit", """{"file_path":"src/b.cs","edits":[]}""", null, "/w/src/b.cs")]
    [InlineData("NotebookEdit", """{"notebook_path":"/w/n.ipynb","new_source":"x"}""", null, "/w/n.ipynb")]
    [InlineData("Read", """{"file_path":"/w/a.py"}""", """{"type":"text"}""", null)]
    [InlineData("Bash", """{"command":"touch x"}""", null, null)]
    public void A_file_tool_names_the_file_it_wrote(string claude, string input, string? result, string? expected)
        => ClaudeCodeTools.WrittenFile(claude, Json(input), result is null ? null : Json(result), "/w").ShouldBe(expected);

    [Fact]
    public void A_write_says_the_whole_file_it_wrote()
        => ClaudeCodeTools.Changes("Write", Json("""{"file_path":"plan.md","content":"- [ ] 1. One\n"}"""), null, "/w/plan.md")
            .ShouldBe([new FileChange { Path = "/w/plan.md", Content = "- [ ] 1. One\n" }]);

    [Theory]
    [InlineData("""{"file_path":"/w/plan.md","old_string":"- [ ] 1.","new_string":"- [x] 1.","replace_all":false}""", false)]
    [InlineData("""{"file_path":"/w/plan.md","old_string":"- [ ] 1.","new_string":"- [x] 1."}""", false)]
    [InlineData("""{"file_path":"/w/plan.md","old_string":"- [ ] 1.","new_string":"- [x] 1.","replace_all":true}""", true)]
    public void An_edit_says_the_text_it_replaced_and_how_often(string input, bool replaceAll)
        => ClaudeCodeTools.Changes("Edit", Json(input), null, "/w/plan.md")
            .ShouldBe([new FileChange { Path = "/w/plan.md", OldString = "- [ ] 1.", NewString = "- [x] 1.", ReplaceAll = replaceAll }]);

    [Fact]
    public void What_Claude_Code_reports_it_did_comes_before_what_the_call_asked_for()
    {
        // The user changed the edit when asked: Claude Code applied "- [~] 1." instead.
        ClaudeCodeTools.Changes(
                "Edit",
                Json("""{"file_path":"/w/plan.md","old_string":"- [ ] 1.","new_string":"- [x] 1."}"""),
                Json("""{"filePath":"/w/plan.md","oldString":"- [ ] 1.","newString":"- [~] 1.","replaceAll":true,"userModified":true}"""),
                "/w/plan.md")
            .ShouldBe([new FileChange { Path = "/w/plan.md", OldString = "- [ ] 1.", NewString = "- [~] 1.", ReplaceAll = true }]);
        ClaudeCodeTools.Changes(
                "Write",
                Json("""{"file_path":"/w/plan.md","content":"asked"}"""),
                Json("""{"type":"update","filePath":"/w/plan.md","content":"written"}"""),
                "/w/plan.md")
            .ShouldBe([new FileChange { Path = "/w/plan.md", Content = "written" }]);
    }

    [Fact]
    public void A_multi_edit_says_each_edit_in_the_order_it_makes_them()
        => ClaudeCodeTools.Changes("MultiEdit", Json("""
            {"file_path":"/w/plan.md","edits":[
              {"old_string":"- [ ] 1.","new_string":"- [x] 1."},
              {"old_string":"[ ]","new_string":"[x]","replace_all":true}]}
            """), null, "/w/plan.md")
            .ShouldBe([
                new FileChange { Path = "/w/plan.md", OldString = "- [ ] 1.", NewString = "- [x] 1." },
                new FileChange { Path = "/w/plan.md", OldString = "[ ]", NewString = "[x]", ReplaceAll = true },
            ]);

    [Theory]
    // An edit with no text to find makes a new file; Fleet can't apply it to what it knows, so it reads the file.
    [InlineData("Edit", """{"file_path":"/w/plan.md","old_string":"","new_string":"- [ ] 1."}""")]
    // One edit Fleet can't apply would leave the ones after it applied to the wrong text.
    [InlineData("MultiEdit", """{"file_path":"/w/plan.md","edits":[{"old_string":"","new_string":"a"},{"old_string":"a","new_string":"b"}]}""")]
    [InlineData("Write", """{"file_path":"/w/plan.md"}""")]
    [InlineData("NotebookEdit", """{"notebook_path":"/w/n.ipynb","new_source":"x"}""")]
    [InlineData("Read", """{"file_path":"/w/plan.md"}""")]
    public void A_call_that_says_nothing_Fleet_can_apply_has_no_changes(string claude, string input)
        => ClaudeCodeTools.Changes(claude, Json(input), null, "/w/plan.md").ShouldBeEmpty();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
