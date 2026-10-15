using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Acp;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Acp;

public sealed class AcpMapperTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Chunk(string kind, string text)
        => JsonSerializer.SerializeToElement(new { sessionUpdate = kind, content = new { type = "text", text } });

    [Fact]
    public void text_streams_as_a_message_with_one_part_that_grows_by_deltas()
    {
        var mapper = new AcpMapper("s1");

        var first = mapper.Update(Chunk("agent_message_chunk", "Hel"));
        var second = mapper.Update(Chunk("agent_message_chunk", "lo"));

        first.Select(e => e.Type).ShouldBe([EventTypes.MessageCreated, EventTypes.MessagePartUpdated]);
        second.Single().Type.ShouldBe(EventTypes.MessagePartDelta);
        second.Single().Payload!.Value.GetProperty("delta").GetString().ShouldBe("lo");
        mapper.Messages().Single().TextContent.ShouldBe("Hello");
    }

    [Fact]
    public void the_finished_message_event_carries_its_parts_so_the_answer_doesnt_vanish()
    {
        var mapper = new AcpMapper("s1");
        mapper.Fusion(J("""{"event":"phase_started","role":"solver","model":"m","index":0}"""));
        mapper.Update(Chunk("agent_message_chunk", "All done."));

        var finished = mapper.EndTurn("end_turn").Single(e => e.Type == EventTypes.MessageUpdated);

        var parts = finished.Payload!.Value.GetProperty("parts");
        parts.GetArrayLength().ShouldBe(2);
        parts[1].GetProperty("text").GetString().ShouldBe("All done.");
    }

    [Fact]
    public void thinking_and_text_are_separate_parts_in_the_order_they_came()
    {
        var mapper = new AcpMapper("s1");

        mapper.Update(Chunk("agent_thought_chunk", "hmm"));
        mapper.Update(Chunk("agent_message_chunk", "ok"));

        mapper.Messages().Single().Parts.Select(p => p.GetType().Name).ShouldBe(["ReasoningPart", "TextPart"]);
    }

    [Fact]
    public void an_edit_runs_then_completes_and_reports_the_file_it_wrote()
    {
        var mapper = new AcpMapper("s1");

        var started = mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"Write notes.md","kind":"edit","status":"in_progress","locations":[{"path":"/work/notes.md"}]}"""));
        var done = mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed","content":[{"type":"diff","path":"/work/notes.md","oldText":"","newText":"hi"}]}"""));

        State(started[^1]).ShouldBe("running");
        State(done.First(e => e.Type == EventTypes.MessagePartUpdated)).ShouldBe("completed");
        done[^1].Type.ShouldBe(EventTypes.FilesWritten);
        done[^1].Payload!.Value.GetProperty("paths")[0].GetString().ShouldBe("/work/notes.md");
        var tool = mapper.Messages().Single().Parts.OfType<ToolUsePart>().Single();
        tool.ToolName.ShouldBe("edit");
        tool.State.ShouldBe(ToolUseState.Completed);
        tool.Arguments.GetProperty("path").GetString().ShouldBe("/work/notes.md");
    }

    [Fact]
    public void a_failed_call_carries_the_reason()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"Run tests","kind":"execute","status":"in_progress"}"""));

        var events = mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"failed","rawOutput":{"message":"The user rejected this tool call."}}"""));

        State(events.Single()).ShouldBe("error");
        mapper.Messages().Single().Parts.OfType<ToolUsePart>().Single().Error.ShouldBe("The user rejected this tool call.");
    }

    [Fact]
    public void ending_a_turn_settles_calls_still_open_and_goes_idle()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"Sleep","kind":"execute","status":"in_progress"}"""));

        var events = mapper.EndTurn("cancelled");

        State(events[0]).ShouldBe("error");
        events.Select(e => e.Type).ShouldBe([EventTypes.MessagePartUpdated, EventTypes.MessageUpdated, EventTypes.SessionStatus]);
        events[^1].Payload!.Value.GetProperty("status").GetProperty("type").GetString().ShouldBe("idle");
    }

    [Fact]
    public void hydrafusion_passes_become_rows_that_run_then_finish_with_time_and_credits()
    {
        var mapper = new AcpMapper("s1");

        var started = mapper.Fusion(J("""{"sessionId":"s","event":"phase_started","pattern":"cascade","role":"draft","model":"model-a","index":0}"""));
        var finished = mapper.Fusion(J("""{"sessionId":"s","event":"phase_completed","pattern":"cascade","role":"draft","model":"model-a","index":0,"status":"succeeded","durationMs":18400,"credits":4.031}"""));
        var other = mapper.Fusion(J("""{"sessionId":"s","event":"resolved","pattern":"cascade"}"""));

        State(started[^1]).ShouldBe("running");
        State(finished[^1]).ShouldBe("completed");
        other.ShouldBeEmpty();
        var pass = mapper.Messages().Single().Parts.OfType<ToolUsePart>().Single();
        pass.ToolName.ShouldBe(AcpMapper.PassTool);
        pass.Arguments.GetProperty("durationMs").GetInt64().ShouldBe(18400);
        pass.Arguments.GetProperty("credits").GetDouble().ShouldBe(4.03);
    }

    [Fact]
    public void a_pass_without_usage_sends_null_numbers_and_still_maps()
    {
        var mapper = new AcpMapper("s1");

        var events = mapper.Fusion(J("""{"event":"phase_completed","role":"judge","model":"m","index":"one","status":"succeeded","durationMs":null,"credits":null}"""));

        State(events[^1]).ShouldBe("completed");
    }

    [Fact]
    public void a_refused_pass_shows_as_an_error_row()
    {
        var mapper = new AcpMapper("s1");

        var events = mapper.Fusion(J("""{"event":"phase_completed","role":"judge","model":"m","index":1,"status":"failed"}"""));

        State(events[^1]).ShouldBe("error");
    }

    [Fact]
    public void a_failed_turn_gives_a_failure_card_with_the_readable_message_then_idle()
    {
        var mapper = new AcpMapper("s1");

        var events = mapper.Fail("Model \"hydrafusion\" is not available.");

        events.Select(e => e.Type).ShouldBe([EventTypes.SessionError, EventTypes.SessionStatus]);
        events[0].Payload!.Value.GetProperty("error").GetProperty("message").GetString().ShouldBe("Model \"hydrafusion\" is not available.");
    }

    [Fact]
    public void a_replayed_conversation_becomes_user_and_assistant_messages()
    {
        var mapper = new AcpMapper("s1");

        mapper.Update(Chunk("user_message_chunk", "Add a "));
        mapper.Update(Chunk("user_message_chunk", "retry"));
        mapper.Update(Chunk("agent_message_chunk", "Done."));
        mapper.Update(Chunk("user_message_chunk", "Thanks"));

        mapper.Messages().Select(m => (m.Role, m.TextContent)).ShouldBe([("user", "Add a retry"), ("assistant", "Done."), ("user", "Thanks")]);
    }

    private static string State(HarnessEvent evt)
        => evt.Payload!.Value.GetProperty("part").GetProperty("state").GetProperty("status").GetString()!;

    [Fact]
    public void a_steer_mid_turn_completes_the_message_and_settles_its_running_calls()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(Chunk("agent_message_chunk", "Looking."));
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","kind":"execute","title":"sleep","status":"in_progress"}"""));

        var steer = mapper.UserPrompt("never mind");

        steer.Select(e => e.Type).ShouldBe([EventTypes.MessagePartUpdated, EventTypes.MessageUpdated]);
        steer[0].Payload!.Value.GetProperty("part").GetProperty("state").GetProperty("status").GetString().ShouldBe("error");
        steer[1].Payload!.Value.GetProperty("info").GetProperty("time").TryGetProperty("completed", out _).ShouldBeTrue();
        mapper.Messages().Select(m => m.Role).ShouldBe(["assistant", "user"]);
    }

    [Fact]
    public void what_the_agent_says_about_a_call_after_the_steer_does_not_reopen_it()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","kind":"execute","status":"in_progress"}"""));
        mapper.UserPrompt("never mind");

        var late = mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed"}"""));

        late.ShouldBeEmpty();
        mapper.Messages().Select(m => m.Role).ShouldBe(["assistant", "user"]);
    }

    [Fact]
    public void a_prompt_between_turns_adds_only_the_user_message()
    {
        var mapper = new AcpMapper("s1");

        mapper.UserPrompt("hello").ShouldBeEmpty();

        mapper.Messages().Single().Role.ShouldBe("user");
    }
}
