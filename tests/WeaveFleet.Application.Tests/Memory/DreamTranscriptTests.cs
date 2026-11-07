using System.Text.Json;
using Shouldly;
using WeaveFleet.Application.Memory.Dreaming;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Tests.Memory;

public sealed class DreamTranscriptTests
{
    private static int _n;

    private static MessageLifecyclePayload Message(string role, params MessageEventPart[] parts)
        => new() { Info = new MessageEventInfo { Id = $"m{_n++}", Role = role, SessionId = "s", Time = new MessageEventTime { Created = _n } }, Parts = parts };

    private static MessageLifecyclePayload User(string text) => Message("user", Text(text));

    private static TextMessageEventPart Text(string text) => new() { Id = $"p{_n++}", SessionId = "s", MessageId = "m", Text = text };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ToolMessageEventPart Tool(string name, ToolInvocationState state)
        => new() { Id = $"p{_n++}", SessionId = "s", MessageId = "m", ToolName = name, CallId = $"c{_n}", State = state };

    private static ToolCompletedState Done(string input, string? output = null, string? metadata = null)
        => new() { Input = Json(input), Output = output is null ? null : JsonSerializer.SerializeToElement(output), Metadata = metadata is null ? null : Json(metadata) };

    [Fact]
    public void Failed_calls_show_the_command_and_the_tail_of_the_error()
    {
        var error = new string('x', 500) + "ENOENT: no such file";
        var text = DreamTranscript.Condense(
        [
            User("run the tests"),
            Message("assistant", Tool("read", new ToolErrorState { Input = Json("""{"path":"a.txt"}"""), Error = error })),
            Message("assistant", Text("Done, they pass.")),
        ]);

        text.ShouldContain("You: run the tests");
        text.ShouldContain("Failed read: a.txt");
        var arrow = text.Split('\n').Single(l => l.StartsWith("  → ", StringComparison.Ordinal));
        arrow.Length.ShouldBeLessThanOrEqualTo(4 + 400);
        arrow.ShouldEndWith("ENOENT: no such file");
        text.ShouldEndWith("Agent: Done, they pass.");
    }

    [Fact]
    public void A_shell_call_with_a_nonzero_exit_failed_and_running_it_again_is_marked()
    {
        var text = DreamTranscript.Condense(
        [
            User("build it"),
            Message("assistant",
                Tool("bash", Done("""{"command":"npm test"}""", "sh: npm: command not found", """{"exit":127}""")),
                Tool("bash", Done("""{"command":"bun run test"}""", "ok", """{"exit":0}""")),
                Tool("bash", Done("""{"command":"npm test"}""", "ok"))),
        ]);

        var lines = text.Split('\n');
        lines.ShouldContain("Failed bash: npm test");
        lines.ShouldContain("  → sh: npm: command not found");
        lines.ShouldContain("Ran again: npm test");
        text.ShouldNotContain("bun run test");
    }

    [Fact]
    public void Exit_codes_in_the_output_count_and_a_zero_exit_does_not()
    {
        var text = DreamTranscript.Condense(
        [
            Message("assistant",
                Tool("bash", Done("""{"command":"make"}""", "building\nprocess exited with code 2")),
                Tool("bash", Done("""{"command":"ls"}""", "fine\nexit code 0"))),
        ]);

        text.ShouldContain("Failed bash: make");
        text.ShouldNotContain("ls");
    }

    [Theory]
    [InlineData("fleet_memory_save")]
    [InlineData("mcp__fleet__fleet_memory_save")]
    public void Memory_saves_and_forgets_are_listed_under_any_harness_spelling(string tool)
    {
        var forget = tool.Replace("save", "forget", StringComparison.Ordinal);
        var text = DreamTranscript.Condense(
        [
            Message("assistant",
                Tool(tool, Done("""{"list":"repository","text":"Use bun."}""")),
                Tool(forget, Done("""{"id":"ab12cd34"}"""))),
        ]);

        text.ShouldContain("Saved note: Use bun.");
        text.ShouldContain("Forgot note ab12cd34");
    }

    [Fact]
    public void Prompts_and_replies_are_cut_and_a_session_is_capped()
    {
        var long600 = DreamTranscript.Condense([User(new string('a', 1000))]);
        long600.Length.ShouldBeLessThanOrEqualTo("You: ".Length + 600);

        var many = Enumerable.Range(0, 100).Select(i => User($"prompt {i} " + new string('z', 500))).ToList();
        DreamTranscript.Condense(many).Length.ShouldBeLessThanOrEqualTo(DreamRules.MaxSessionChars + 2);

        var reply = DreamTranscript.Condense([User("hi"), Message("assistant", Text(new string('r', 500)))]);
        reply.Split('\n').Last().Length.ShouldBeLessThanOrEqualTo("Agent: ".Length + 200);
    }

    [Fact]
    public void The_set_is_capped_by_dropping_the_oldest_and_labels_follow_what_is_kept()
    {
        var sessions = Enumerable.Range(0, 30)
            .Select(i => new TranscriptSession($"id{i}", $"Session {i}", "opencode2",
                [.. Enumerable.Range(0, 8).Select(j => User($"{j} " + new string('q', 590)))]))
            .ToList();

        var result = DreamTranscript.Build(sessions);

        result.Text.Length.ShouldBeLessThanOrEqualTo(DreamRules.MaxTotalChars + 200);
        result.Used.Count.ShouldBeLessThan(30);
        result.Used[0].Id.ShouldBe("id0", "newest first, so the oldest are the ones dropped");
        result.Text.ShouldStartWith("## S1 · Session 0 · opencode2");
        result.Text.ShouldContain($"## S{result.Used.Count} · ");
        result.Text.ShouldNotContain($"## S{result.Used.Count + 1} · ");
    }

    [Fact]
    public void A_session_with_nothing_in_it_is_left_out()
    {
        var result = DreamTranscript.Build([new TranscriptSession("a", "Empty", "opencode2", []), new TranscriptSession("b", "Talked", "claude-code", [User("hello")])]);

        result.Used.Single().Id.ShouldBe("b");
        result.Text.ShouldStartWith("## S1 · Talked · claude-code");
    }

    [Fact]
    public void The_final_message_is_read_from_a_fenced_block_or_a_bare_object_and_garbage_is_no_answer()
    {
        var fenced = DreamAnswer.Parse("Here:\n```json\n{\"changes\":[{\"action\":\"add\",\"list\":\"machine\",\"text\":\"t\",\"sessions\":[\"S1\",\"S2\"],\"corrected\":true}]}\n```\nbye");
        var bare = DreamAnswer.Parse("No fence. {\"changes\":[]} trailing");
        var last = DreamAnswer.Parse("```json\n{\"changes\":[{\"action\":\"retire\"}]}\n```\nthen\n```json\n{\"changes\":[]}\n```");

        fenced.ShouldNotBeNull().Single().Action.ShouldBe("add");
        fenced[0].Sessions.ShouldBe(["S1", "S2"]);
        fenced[0].Corrected.ShouldBeTrue();
        bare.ShouldNotBeNull().ShouldBeEmpty();
        last.ShouldNotBeNull().ShouldBeEmpty();
        DreamAnswer.Parse("I could not find anything.").ShouldBeNull();
        DreamAnswer.Parse("```json\n{not json}\n```").ShouldBeNull();
        DreamAnswer.Parse("```json\n{\"other\":1}\n```").ShouldBeNull();
        DreamAnswer.Parse(null).ShouldBeNull();
    }

    [Fact]
    public void The_answer_reads_a_keep_action_and_the_prompt_offers_it_and_marks_notes_that_fade()
    {
        var proposals = DreamAnswer.Parse("```json\n{\"changes\":[{\"action\":\"keep\",\"note\":\"aa11\",\"why\":\"w\",\"sessions\":[\"S1\",\"S2\",\"S3\"],\"quote\":\"q\"}]}\n```");

        var keep = proposals.ShouldNotBeNull().Single();
        keep.Action.ShouldBe("keep");
        keep.Note.ShouldBe("aa11");
        keep.Sessions.ShouldBe(["S1", "S2", "S3"]);

        var prompt = DreamPrompt.Build("/work/fleet", "", 3,
            [new DreamNote { Id = "aa11", List = "repository", Kind = "learned", Text = "Use bun.", Fades = true }]);
        prompt.ShouldContain("\"action\":\"keep\"");
        prompt.ShouldContain("[aa11] (learned, fades, untested) Use bun.");
    }

    [Fact]
    public void A_prompt_for_a_read_only_session_asks_for_checks_and_names_the_sessions_ids()
    {
        var prompt = DreamPrompt.Build("/work/fleet", "## S1 · Fix it · opencode2\nYou: hi", 2,
            [new DreamNote { Id = "aa11", List = "repository", Kind = "from-you", Text = "Build with make all." }],
            ["session-aaa", "session-bbb"], readsRepository: true);

        prompt.ShouldContain("CHECK the notes");
        prompt.ShouldContain("git log");
        prompt.ShouldContain("fleet_session_read");
        prompt.ShouldContain("S1 = session-aaa, S2 = session-bbb");
        prompt.ShouldContain("\"action\":\"flag\"");
        prompt.ShouldContain("\"evidence\"");
        prompt.ShouldNotContain("Don't run commands");
    }

    [Fact]
    public void A_prompt_for_a_session_that_cannot_be_made_read_only_keeps_the_no_tools_instruction()
    {
        var prompt = DreamPrompt.Build("/work/fleet", "", 2, [], ["session-aaa", "session-bbb"], readsRepository: false);

        prompt.ShouldContain("Don't run commands, don't edit files");
        prompt.ShouldNotContain("fleet_session_read");
        prompt.ShouldNotContain("session-aaa");
    }

    [Fact]
    public void The_answer_reads_a_flag_with_its_evidence()
    {
        var flag = DreamAnswer.Parse("```json\n{\"changes\":[{\"action\":\"flag\",\"note\":\"aa11\",\"why\":\"w\",\"sessions\":[\"S2\"],\"quote\":\"q\",\"evidence\":\"Makefile deleted in 4f2a9c1\"}]}\n```")
            .ShouldNotBeNull().Single();

        flag.Action.ShouldBe("flag");
        flag.Evidence.ShouldBe("Makefile deleted in 4f2a9c1");
    }

    [Fact]
    public void The_prompt_shows_how_each_learned_note_is_known_and_checks_the_untested_first()
    {
        var prompt = DreamPrompt.Build("/work/fleet", "", 3,
        [
            new DreamNote { Id = "aa11", List = "repository", Kind = "learned", Text = "Use bun.", Fades = true, How = "ran bun run test: ok" },
            new DreamNote { Id = "bb22", List = "repository", Kind = "learned", Text = "Build with make." },
            new DreamNote { Id = "cc33", List = "repository", Kind = "from-you", Text = "Never npm." },
        ], readsRepository: true);

        prompt.ShouldContain("[aa11] (learned, fades, tested: ran bun run test: ok) Use bun.");
        prompt.ShouldContain("[bb22] (learned, untested) Build with make.");
        prompt.ShouldContain("[cc33] (from-you) Never npm.");
        prompt.ShouldContain("Check the untested learned notes first");
        prompt.ShouldContain("\"how\"");
    }

    [Fact]
    public void The_answer_reads_how_and_tolerates_it_missing()
    {
        var proposals = DreamAnswer.Parse("```json\n{\"changes\":[{\"action\":\"rewrite\",\"note\":\"a\",\"text\":\"t\",\"how\":\"checked: x exists\"},{\"action\":\"add\",\"text\":\"t\"}]}\n```");

        proposals.ShouldNotBeNull().Count.ShouldBe(2);
        proposals[0].How.ShouldBe("checked: x exists");
        proposals[1].How.ShouldBeNull();
    }
}
