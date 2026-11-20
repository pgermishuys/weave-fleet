using WeaveFleet.Application.TerminalSessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// The nightly format check (<c>.github/workflows/claude-transcript-check.yml</c>): transcripts a real, newest Claude Code
/// wrote against a fake Anthropic API (<c>scripts/claude-transcript-check/run.sh</c>), read by Fleet's reader. Without
/// <c>FLEET_CLAUDE_TRANSCRIPTS</c> (the folder run.sh filled) the check has nothing to read and passes. Every failure says
/// which transcript and what the reader didn't understand.
/// </summary>
public sealed class ClaudeTranscriptFormatCheckTests
{
    private const string Folder = "FLEET_CLAUDE_TRANSCRIPTS";

    private static Dictionary<string, TerminalTranscript> ReadAll()
    {
        var folder = Environment.GetEnvironmentVariable(Folder);
        if (string.IsNullOrWhiteSpace(folder))
            return [];

        var files = Directory.GetFiles(folder, "*.jsonl");
        Assert.True(files.Length > 0, $"{Folder}={folder} holds no transcripts: did scripts/claude-transcript-check/run.sh run?");
        return files.ToDictionary(f => Path.GetFileNameWithoutExtension(f)!, f => ClaudeTranscriptReader.Read(File.ReadAllLines(f)));
    }

    [Fact]
    public void The_reader_understands_every_entry_the_newest_Claude_Code_writes()
    {
        var unknown = ReadAll()
            .Where(t => t.Value.Unknown.Count > 0)
            .Select(t => $"{t.Key}.jsonl: {string.Join(", ", t.Value.Unknown)}")
            .ToList();

        Assert.True(unknown.Count == 0,
            "Fleet's transcript reader met entries or content it has no rule for (a Claude Code format change):\n  " + string.Join("\n  ", unknown));
    }

    [Fact]
    public void A_plain_resumed_session_reads_as_its_two_turns()
    {
        if (!ReadAll().TryGetValue("plain-resumed", out var t))
            return;

        Assert.True(t.Messages.Select(m => m.Role).SequenceEqual(["user", "assistant", "user", "assistant"]),
            $"plain-resumed.jsonl: expected user, assistant, user, assistant; read {string.Join(", ", t.Messages.Select(m => m.Role))}");
        Assert.Equal("Say hello.", t.Messages[0].TextContent);
        Assert.Equal("Say it again.", t.Messages[2].TextContent);
        Assert.StartsWith("Fake reply to: Say hello.", t.Messages[1].TextContent);
        Assert.False(string.IsNullOrEmpty(t.Folder), "plain-resumed.jsonl: the reader found no folder (cwd) in any entry");
    }

    [Fact]
    public void A_tool_call_reads_as_a_call_with_its_result()
    {
        if (!ReadAll().TryGetValue("tool", out var t))
            return;

        var parts = t.Messages.SelectMany(m => m.Parts).ToList();
        Assert.True(parts.OfType<ToolUsePart>().Any(p => p.ToolName == "read"), "tool.jsonl: no read tool call (the Read the fake API asked for)");
        Assert.True(parts.OfType<ToolResultPart>().Any(p => (p.Content ?? "").Contains("hello from the fixture file")),
            "tool.jsonl: the read's result isn't in the conversation");
    }

    [Fact]
    public void A_compacted_session_keeps_the_turns_before_the_compaction()
    {
        if (!ReadAll().TryGetValue("compact", out var t))
            return;

        var texts = t.Messages.Select(m => m.TextContent ?? "").ToList();
        Assert.True(texts.Any(x => x.Contains("Say hello.")), "compact.jsonl: the turns before /compact are gone");
        Assert.True(texts.Any(x => x.Contains("And now?")), "compact.jsonl: the turn after /compact is missing");
    }

    [Fact]
    public void An_entry_type_the_reader_has_no_rule_for_is_named()
    {
        var lines = new[]
        {
            """{"type":"user","uuid":"a","parentUuid":null,"cwd":"/w","message":{"role":"user","content":"hi"}}""",
            """{"type":"hologram","uuid":"b","parentUuid":"a"}""",
        };

        Assert.Contains("entry:hologram", ClaudeTranscriptReader.Read(lines).Unknown);
    }
}
