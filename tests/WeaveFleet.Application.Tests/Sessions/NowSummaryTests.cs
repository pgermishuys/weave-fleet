using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Sessions;

namespace WeaveFleet.Application.Tests.Sessions;

/// <summary>The now line names a tool and its short target, in one line, and keeps secrets and contents off it.</summary>
public sealed class NowSummaryTests
{
    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("bash", """{"command":"pnpm test webhooks"}""", "bash · pnpm test webhooks")]
    [InlineData("Bash", """{"command":"dotnet build\nthen more"}""", "bash · dotnet build")]
    [InlineData("edit", """{"filePath":"src/sync/rules.ts","oldString":"a","newString":"b"}""", "edit · src/sync/rules.ts")]
    [InlineData("write", """{"file_path":"notes.md","content":"top secret body"}""", "write · notes.md")]
    [InlineData("read", """{"path":"README.md"}""", "read · README.md")]
    [InlineData("grep", """{"pattern":"TODO","path":"src"}""", "grep · TODO")]
    [InlineData("webfetch", """{"url":"https://example.com/docs"}""", "webfetch · https://example.com/docs")]
    [InlineData("task", """{"description":"Check the ledger tests","prompt":"long prompt"}""", "task · Check the ledger tests")]
    [InlineData("todowrite", """{"todos":[{"content":"x"}]}""", "todowrite")]
    public void A_tool_call_reads_as_tool_and_target(string tool, string input, string expected) =>
        NowSummary.FromTool(tool, Input(input)).ShouldBe(expected);

    [Fact]
    public void A_tool_with_no_input_is_just_its_name() =>
        NowSummary.FromTool("Glob", null).ShouldBe("glob");

    [Fact]
    public void A_reply_is_its_first_line() =>
        NowSummary.FromReply("All 12 tests pass.\nI also tidied the imports.").ShouldBe("All 12 tests pass.");

    [Fact]
    public void A_long_line_is_cut_to_the_cap_with_an_ellipsis()
    {
        var line = NowSummary.FromTool("bash", Input($$"""{"command":"{{new string('x', 400)}}"}"""));

        line.Length.ShouldBe(NowSummary.MaxLength);
        line.ShouldEndWith("…");
    }

    [Theory]
    [InlineData("export GH_TOKEN=ghp_abc123 && gh pr list", "bash · export GH_TOKEN=*** && gh pr list")]
    [InlineData("API_KEY='a b c' ./run.sh", "bash · API_KEY=*** ./run.sh")]
    [InlineData("DB_PASSWORD=\"p w\" psql", "bash · DB_PASSWORD=*** psql")]
    [InlineData("client_secret=zzz curl x", "bash · client_secret=*** curl x")]
    [InlineData("curl -H 'Authorization: Bearer abc.def' https://x.test", "bash · curl -H 'Authorization: Bearer ***' https://x.test")]
    [InlineData("curl -H Authorization:\\ Basic dXNlcjpwdw== x", "bash · curl -H Authorization:\\ Basic *** x")]
    [InlineData("PORT=8080 node app.js", "bash · PORT=8080 node app.js")]
    public void Secrets_in_a_command_line_are_masked(string command, string expected) =>
        NowSummary.FromTool("bash", Input(JsonSerializer.Serialize(new { command }))).ShouldBe(expected);

    [Fact]
    public void A_secret_past_the_cap_is_masked_before_the_cut()
    {
        var line = NowSummary.FromTool("bash", Input($$"""{"command":"echo {{new string('a', 100)}} TOKEN=abcdefghijklmnopqrstuvwxyz"}"""));

        line.ShouldNotContain("abcdef");
    }
}
