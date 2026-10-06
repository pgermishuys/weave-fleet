using System.Text.Json;
using Shouldly;
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

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
