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
}
