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
        var other = mapper.Fusion(J("""{"sessionId":"s","event":"route_failed"}"""));

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

    [Fact]
    public void a_critique_turn_has_a_head_row_with_its_workflow_and_credits_and_pass_rows_with_verdicts()
    {
        var mapper = new AcpMapper("s1");

        var resolved = mapper.Fusion(J("""{"event":"resolved","pattern":"critique","model":"model-a","secondaryModel":"model-b","hint":"Using Critique","index":0}"""));
        mapper.Fusion(J("""{"event":"phase_completed","pattern":"critique","role":"solver","model":"model-a","index":0,"status":"succeeded","credits":13.32}"""));
        mapper.Fusion(J("""{"event":"phase_completed","role":"critic","model":"model-b","index":1,"status":"succeeded","verdict":"needs repair","credits":0.2,"durationMs":43042}"""));
        var completed = mapper.Fusion(J("""{"event":"completed","pattern":"critique","model":"model-a","status":"completed","totalCredits":16.8274,"requests":7,"durationMs":69434}"""));

        State(resolved[^1]).ShouldBe("running");
        State(completed[^1]).ShouldBe("completed");
        var rows = mapper.Messages().Single().Parts.OfType<ToolUsePart>().ToList();
        rows.Select(r => r.ToolName).ShouldBe([AcpMapper.TurnTool, AcpMapper.PassTool, AcpMapper.PassTool]);
        rows[0].Arguments.GetProperty("pattern").GetString().ShouldBe("critique");
        rows[0].Arguments.GetProperty("credits").GetDouble().ShouldBe(16.83);
        rows[0].Arguments.GetProperty("requests").GetInt64().ShouldBe(7);
        rows[2].Arguments.GetProperty("verdict").GetString().ShouldBe("needs repair");
    }

    [Fact]
    public void a_failed_pass_is_an_error_row_that_says_why()
    {
        var mapper = new AcpMapper("s1");

        var events = mapper.Fusion(J("""{"event":"phase_completed","role":"critic","model":"m","index":1,"status":"failed","reason":"The critic timed out"}"""));

        State(events[^1]).ShouldBe("error");
        mapper.Messages().Single().Parts.OfType<ToolUsePart>().Single().Error.ShouldBe("The critic timed out");
    }

    [Fact]
    public void a_replayed_turn_without_pass_starts_rebuilds_the_same_rows()
    {
        var mapper = new AcpMapper("s1");

        mapper.Update(J("""{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"add a test"}}"""));
        mapper.Fusion(J("""{"event":"resolved","pattern":"cascade","model":"model-a","index":0}"""));
        mapper.Fusion(J("""{"event":"phase_completed","role":"solver","model":"model-a","index":0,"status":"succeeded"}"""));
        mapper.Fusion(J("""{"event":"phase_completed","role":"judge","model":"model-b","index":1,"status":"succeeded","verdict":"approved"}"""));
        mapper.Fusion(J("""{"event":"completed","pattern":"cascade","status":"completed","totalCredits":4.07,"requests":4}"""));
        mapper.Update(Chunk("agent_message_chunk", "Done."));

        var turn = mapper.Messages()[1];
        turn.Parts.OfType<ToolUsePart>().Select(r => r.State).ShouldAllBe(state => state == ToolUseState.Completed);
        turn.Parts.OfType<ToolUsePart>().Count().ShouldBe(3);
        turn.TextContent.ShouldBe("Done.");
    }

    [Fact]
    public void the_turns_usage_becomes_a_step_finish_part_the_status_bar_adds_up()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(Chunk("agent_message_chunk", "ok"));

        var events = mapper.Usage(J("""{"inputTokens":139779,"outputTokens":4882,"thoughtTokens":100,"totalTokens":144661}"""));

        var part = events.Single().Payload!.Value.GetProperty("part");
        part.GetProperty("type").GetString().ShouldBe("step-finish");
        part.GetProperty("tokens").GetProperty("input").GetDouble().ShouldBe(139779);
        part.GetProperty("tokens").GetProperty("output").GetDouble().ShouldBe(4782);
        part.GetProperty("tokens").GetProperty("reasoning").GetDouble().ShouldBe(100);
        mapper.Messages().Single().Parts.OfType<StepFinishPart>().Single().TokensInput.ShouldBe(139779);
        // the finished message keeps the step, so the answer's tokens don't vanish with its last update
        mapper.EndTurn("end_turn").Single(e => e.Type == EventTypes.MessageUpdated).Payload!.Value.GetProperty("parts").EnumerateArray()
            .Select(x => x.GetProperty("type").GetString()).ShouldContain("step-finish");
    }

    [Fact]
    public void a_plain_turn_that_cost_credits_gets_a_head_row_but_a_fusion_turn_does_not_get_a_second()
    {
        var plain = new AcpMapper("s1");
        plain.Update(Chunk("agent_message_chunk", "ok"));
        plain.Usage(J("""{"inputTokens":10,"outputTokens":5,"credits":0.25}"""));
        var row = plain.Messages().Single().Parts.OfType<ToolUsePart>().Single();
        row.ToolName.ShouldBe(AcpMapper.TurnTool);
        row.Arguments.GetProperty("credits").GetDouble().ShouldBe(0.25);

        var fusion = new AcpMapper("s2");
        fusion.Fusion(J("""{"event":"resolved","pattern":"single","model":"m"}"""));
        fusion.Fusion(J("""{"event":"completed","pattern":"single","status":"completed","totalCredits":0.16}"""));
        fusion.Usage(J("""{"inputTokens":10,"outputTokens":5,"credits":0.16}"""));
        fusion.Messages().Single().Parts.OfType<ToolUsePart>().Count().ShouldBe(1);

        new AcpMapper("s3").Usage(J("""{"inputTokens":0,"outputTokens":0}""")).ShouldBeEmpty();
    }

    private static string State(HarnessEvent evt)
        => evt.Payload!.Value.GetProperty("part").GetProperty("state").GetProperty("status").GetString()!;
}
