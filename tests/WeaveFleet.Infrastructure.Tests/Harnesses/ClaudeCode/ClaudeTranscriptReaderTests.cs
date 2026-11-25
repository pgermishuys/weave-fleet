using WeaveFleet.Application.TerminalSessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// Claude Code's session transcript read into Fleet's messages (<c>Fixtures/ClaudeCode/transcripts</c>, in the shape Claude
/// Code 2.1.329 writes, with invented content): the SDK's rules for which entries are the conversation, and ids that don't
/// change from one read to the next.
/// </summary>
public sealed class ClaudeTranscriptReaderTests
{
    private static TerminalTranscript Read(string name)
        => ClaudeTranscriptReader.Read(File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", "transcripts", name)));

    [Fact]
    public void A_background_task_notice_Claude_Code_wrote_as_a_user_entry_is_not_the_users_message()
    {
        var transcript = Read("background-notices.jsonl");

        Assert.Equal(["user", "assistant", "assistant", "assistant", "assistant"], transcript.Messages.Select(m => m.Role));
        Assert.DoesNotContain(transcript.Messages, m => m.TextContent.Contains("<task-notification>"));
        Assert.Equal("Water the orchard in the background and tell me when it is done.", transcript.Messages[0].TextContent);
        Assert.Contains(transcript.Messages, m => m.TextContent == "The watering finished.");
        Assert.Contains(transcript.Messages, m => m.TextContent == "The pump was stopped.");
    }

    [Fact]
    public void A_plain_conversation_is_its_prompts_and_replies_and_nothing_else()
    {
        var transcript = Read("plain.jsonl");

        Assert.Equal(["user", "assistant", "user", "assistant"], transcript.Messages.Select(m => m.Role));
        Assert.Equal(
            ["What is the plan for the apple harvest?", "Pick the north rows first, then the creek rows.", "And the pears?", "Pears wait a week."],
            transcript.Messages.Select(m => m.TextContent));
        Assert.Equal("claude-haiku-5-5", transcript.Messages[1].ModelId);
        Assert.Equal("/work/orchard", transcript.Folder);
        Assert.Equal("Orchard harvest plan", transcript.Title);
        Assert.Empty(transcript.Unknown);
    }

    [Fact]
    public void Each_message_says_which_transcript_entries_it_was_made_from()
    {
        var transcript = Read("tools.jsonl");

        // Every entry that is part of the conversation belongs to exactly one message: its reply's lines and tool results
        // are with the message they continue.
        Assert.All(transcript.Messages, m => Assert.NotEmpty(transcript.Entries[m.Id]));
        var all = transcript.Entries.Values.SelectMany(u => u).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.True(transcript.Entries[transcript.Messages.First(m => m.Role == "assistant").Id].Count >= 1);
    }

    [Fact]
    public void A_prompt_Fleet_sends_carries_the_uuid_it_chose_so_the_transcript_entry_can_be_recognised()
    {
        var line = System.Text.Json.JsonDocument.Parse(ClaudeCodeInput.UserMessage("hello", uuid: "5b0e2b0e-0000-4000-8000-000000000001")).RootElement;

        Assert.Equal("5b0e2b0e-0000-4000-8000-000000000001", line.GetProperty("uuid").GetString());
        Assert.False(System.Text.Json.JsonDocument.Parse(ClaudeCodeInput.UserMessage("hello")).RootElement.TryGetProperty("uuid", out _));
    }

    [Fact]
    public void A_line_cut_off_in_the_middle_of_a_write_is_skipped()
    {
        // plain.jsonl ends with half a line, as a transcript does while Claude Code is writing it.
        Assert.Equal(4, Read("plain.jsonl").Messages.Count);
    }

    [Fact]
    public void Ids_ascend_with_the_conversation_and_are_the_same_on_every_read()
    {
        var first = Read("plain.jsonl").Messages.Select(m => m.Id).ToList();
        var again = Read("plain.jsonl").Messages.Select(m => m.Id).ToList();

        Assert.Equal(first, again);
        Assert.Equal(first.OrderBy(id => id, StringComparer.Ordinal), first);
        Assert.Equal(first.Count, first.Distinct().Count());
    }

    [Fact]
    public void A_longer_read_gives_the_messages_it_had_before_the_ids_they_had()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", "transcripts", "plain.jsonl"));
        // The transcript as it was when the first answer had just been written: up to its turn_duration line.
        var early = ClaudeTranscriptReader.Read(lines.Take(4));
        var full = ClaudeTranscriptReader.Read(lines);

        Assert.Equal(2, early.Messages.Count);
        Assert.Equal(early.Messages.Select(m => m.Id), full.Messages.Take(2).Select(m => m.Id));
    }

    [Fact]
    public void A_replys_blocks_are_one_message_with_their_tool_calls_finished_by_the_results()
    {
        var transcript = Read("tools.jsonl");

        var counting = Assert.Single(transcript.Messages, m => m.TextContent == "Counting the crates.");
        Assert.Equal("assistant", counting.Role);
        Assert.Equal(["reasoning", "text", "tooluse", "toolresult"], counting.Parts.Select(p => p.Kind.ToString().ToLowerInvariant()));

        var bash = Assert.IsType<ToolUsePart>(counting.Parts.OfType<ToolUsePart>().Single());
        Assert.Equal("bash", bash.ToolName);
        Assert.Equal(ToolUseState.Completed, bash.State);
        Assert.Equal("42\n", counting.Parts.OfType<ToolResultPart>().Single().Content);
    }

    [Fact]
    public void An_edit_shows_the_patch_Claude_Code_reported_and_a_failed_call_shows_its_error()
    {
        var transcript = Read("tools.jsonl");

        var edit = transcript.Messages.SelectMany(m => m.Parts).OfType<ToolUsePart>().Single(p => p.ToolName == "edit");
        Assert.Equal(ToolUseState.Completed, edit.State);
        Assert.Contains("+harvest", edit.Metadata?.GetProperty("diff").GetString());

        var read = transcript.Messages.SelectMany(m => m.Parts).OfType<ToolUsePart>().Single(p => p.ToolName == "read");
        Assert.Equal(ToolUseState.Error, read.State);
        Assert.Equal("File does not exist.", read.Error);
    }

    [Fact]
    public void An_image_the_user_sent_is_a_file_part_beside_their_words()
    {
        var prompt = Read("image.jsonl").Messages[0];

        var image = Assert.Single(prompt.Parts.OfType<FilePart>());
        Assert.Equal("image/png", image.Mime);
        Assert.StartsWith("data:image/png;base64,", image.Url, StringComparison.Ordinal);
        Assert.Equal("What fruit is this?", prompt.TextContent);
    }

    [Fact]
    public void A_subagents_own_conversation_is_left_out_and_its_call_shows_as_a_task()
    {
        var transcript = Read("subagent.jsonl");

        Assert.DoesNotContain(transcript.Messages, m => m.TextContent == "Count the beehives.");
        Assert.DoesNotContain(transcript.Messages, m => m.TextContent == "There are 7 hives.");
        var task = transcript.Messages.SelectMany(m => m.Parts).OfType<ToolUsePart>().Single();
        Assert.Equal("task", task.ToolName);
        Assert.Equal(ToolUseState.Completed, task.State);
        Assert.Equal("The helper counted 7 beehives.", transcript.Messages[^1].TextContent);
    }

    [Fact]
    public void A_compaction_keeps_the_history_before_it_and_shows_a_divider_where_it_happened()
    {
        var transcript = Read("compact.jsonl");

        Assert.Equal("Plan the cider pressing.", transcript.Messages[0].TextContent);
        var divider = Assert.Single(transcript.Messages, m => m.Parts.OfType<CompactionPart>().Any());
        var compaction = divider.Parts.OfType<CompactionPart>().Single();
        Assert.Equal(18000, compaction.TokensBefore);
        Assert.Equal(2100, compaction.TokensAfter);
        Assert.Equal(ContextCompactionTriggers.Manual, compaction.Trigger);
        // Its summary is the model's note to itself, not the user's words.
        Assert.DoesNotContain(transcript.Messages, m => m.TextContent.Contains("This session is being continued", StringComparison.Ordinal));
        Assert.Equal("Saturday.", transcript.Messages[^1].TextContent);
    }

    [Fact]
    public void A_name_the_user_gave_the_session_wins_and_a_resumed_session_reads_as_one_conversation()
    {
        var transcript = Read("resumed.jsonl");

        Assert.Equal("Market stall sign", transcript.Title);
        Assert.Equal(
            ["Draft the market stall sign.", "Fresh apples, 3 a crate.", "Make it shorter.", "Apples, 3 a crate."],
            transcript.Messages.Select(m => m.TextContent));
    }

    [Fact]
    public void A_branch_left_by_a_rewind_is_not_in_the_conversation()
    {
        var transcript = Read("rewind.jsonl");

        Assert.Equal(
            ["How many trees are in the north rows?", "Sixty.", "How many in the creek rows?", "Forty."],
            transcript.Messages.Select(m => m.TextContent));
    }

    [Fact]
    public void An_entry_type_it_doesnt_know_is_named_once_and_does_not_stop_the_read()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", "transcripts", "plain.jsonl"))
            .Concat(["""{"type":"hologram","uuid":"x"}""", """{"type":"hologram","uuid":"y"}"""]);

        var transcript = ClaudeTranscriptReader.Read(lines);

        Assert.Equal(4, transcript.Messages.Count);
        Assert.Equal(["entry:hologram"], transcript.Unknown);
    }

    [Fact]
    public void A_session_with_no_name_is_titled_by_its_first_prompt()
    {
        var transcript = Read("tools.jsonl");

        Assert.Null(transcript.Title);
        Assert.Equal("Show the crate count and fix the typo in notes.txt", transcript.FirstPrompt);
    }
}
