using System.Text.Json;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;
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
        mapper.Update(Chunk("agent_thought_chunk", "Thinking."));
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
        done[^2].Type.ShouldBe(EventTypes.FilesWritten);
        done[^2].Payload!.Value.GetProperty("paths")[0].GetString().ShouldBe("/work/notes.md");
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

        var rows = mapper.Messages()[1];
        rows.Parts.OfType<ToolUsePart>().Select(r => r.State).ShouldAllBe(state => state == ToolUseState.Completed);
        rows.Parts.OfType<ToolUsePart>().Count().ShouldBe(3);
        mapper.Messages()[2].TextContent.ShouldBe("Done.");
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

    [Fact]
    public void An_mcp_tool_takes_the_real_name_the_agent_gives_in_meta()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c9","title":"fleet: Show page","kind":"other","status":"in_progress","_meta":{"toolName":"fleet_page_show"}}"""));

        mapper.Messages().Single().Parts.OfType<ToolUsePart>().Single().ToolName.ShouldBe("fleet_page_show");
    }

    [Fact]
    public void A_finished_call_to_a_Fleet_tool_takes_the_title_and_page_Fleet_kept_for_it()
    {
        var asked = new List<(string Tool, string Arguments)>();
        var metadata = J("""{"canvasId":null,"version":null,"page":{"id":"pg_0123456789abcdef0123456789abcdef","entry":"index.html"}}""");
        var mapper = new AcpMapper("s1", (tool, arguments) =>
        {
            asked.Add((tool, arguments.GetRawText()));
            return new FleetToolCallRecord("Simple button", metadata);
        });
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c9","title":"fleet: Show page","kind":"other","status":"pending","rawInput":{"path":"/work/button/index.html","title":"Simple button","placement":"conversation"},"_meta":{"toolName":"fleet_page_show"}}"""));
        asked.ShouldBeEmpty();

        var done = mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c9","status":"completed","content":[{"type":"content","content":{"type":"text","text":"Showing \"Simple button\" in the conversation."}}]}"""));

        // The arguments the agent sent, not the row's (which gains a description from the title).
        asked.ShouldBe([("fleet_page_show", """{"path":"/work/button/index.html","title":"Simple button","placement":"conversation"}""")]);
        var tool = mapper.Messages().Single().Parts.OfType<ToolUsePart>().Single();
        tool.Title.ShouldBe("Simple button");
        tool.Metadata!.Value.GetProperty("page").GetProperty("entry").GetString().ShouldBe("index.html");
        var state = done.Single(e => e.Type == EventTypes.MessagePartUpdated).Payload!.Value.GetProperty("part").GetProperty("state");
        state.GetProperty("title").GetString().ShouldBe("Simple button");
        state.GetProperty("metadata").GetProperty("page").GetProperty("id").GetString().ShouldBe("pg_0123456789abcdef0123456789abcdef");

        // The finished message replaces its parts on the page: the call keeps what Fleet kept for it.
        var finished = mapper.EndTurn("end_turn").Single(e => e.Type == EventTypes.MessageUpdated).Payload!.Value.GetProperty("parts");
        var kept = finished.EnumerateArray().Single(part => part.GetProperty("type").GetString() == "tool").GetProperty("state");
        kept.GetProperty("title").GetString().ShouldBe("Simple button");
        kept.GetProperty("metadata").GetProperty("page").GetProperty("entry").GetString().ShouldBe("index.html");
    }

    [Fact]
    public void Only_a_finished_call_to_one_of_Fleets_tools_asks_what_Fleet_kept()
    {
        var asked = 0;
        var mapper = new AcpMapper("s1", (_, _) =>
        {
            asked++;
            return null;
        });
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"Read a.ts","kind":"read","status":"in_progress","rawInput":{"path":"/work/a.ts"}}"""));
        mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed"}"""));
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c2","title":"fleet: Show page","kind":"other","status":"in_progress","rawInput":{"path":"/work/x.html"},"_meta":{"toolName":"fleet_page_show"}}"""));
        mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c2","status":"failed","content":[{"type":"content","content":{"type":"text","text":"No such file."}}]}"""));

        asked.ShouldBe(0);
        var failed = mapper.Messages().Single().Parts.OfType<ToolUsePart>().Last();
        failed.Metadata.ShouldBeNull();
    }

    [Fact]
    public void A_finished_edit_also_tells_the_changes_panel_which_file_changed()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"Edit a.ts","kind":"edit","status":"in_progress","locations":[{"path":"/work/a.ts"}]}"""));
        var done = mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed"}"""));

        done.Select(e => e.Type).ShouldContain(EventTypes.FilesWritten);
        var changed = done.Single(e => e.Type == EventTypes.FileWatcherUpdated).Payload!.Value;
        changed.GetProperty("file").GetString().ShouldBe("/work/a.ts");
        changed.GetProperty("event").GetString().ShouldBe("change");
    }

    private static string State(HarnessEvent evt)
        => evt.Payload!.Value.GetProperty("part").GetProperty("state").GetProperty("status").GetString()!;

    [Fact]
    public void a_steer_mid_turn_completes_the_message_and_leaves_its_running_call_running()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(Chunk("agent_message_chunk", "Looking."));
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","kind":"execute","title":"sleep","status":"in_progress"}"""));

        var steer = mapper.UserPrompt("never mind");

        steer.Select(e => e.Type).ShouldBe([EventTypes.MessageUpdated]);
        steer[0].Payload!.Value.GetProperty("info").GetProperty("time").TryGetProperty("completed", out _).ShouldBeTrue();
        steer[0].Payload!.Value.GetProperty("parts")[1].GetProperty("state").GetProperty("status").GetString().ShouldBe("running");
        mapper.Messages().Select(m => m.Role).ShouldBe(["assistant", "user"]);
    }

    [Fact]
    public void the_result_the_agent_reports_after_a_steer_settles_the_call_in_the_message_it_belongs_to()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","kind":"execute","status":"in_progress"}"""));
        var first = mapper.Messages().Single().Id;
        mapper.UserPrompt("never mind");

        var late = mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"moved to background"}}]}"""));

        State(late.Single()).ShouldBe("completed");
        late.Single().Payload!.Value.GetProperty("part").GetProperty("messageID").GetString().ShouldBe(first);
        mapper.Messages().Select(m => m.Role).ShouldBe(["assistant", "user"]);
        mapper.Messages()[0].Parts.OfType<ToolUsePart>().Single().State.ShouldBe(ToolUseState.Completed);
        mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed"}""")).ShouldBeEmpty();
    }

    [Fact]
    public void a_call_still_running_when_the_turn_ends_after_a_steer_is_settled_as_not_finished()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","kind":"execute","status":"in_progress"}"""));
        mapper.UserPrompt("never mind");

        var end = mapper.EndTurn("end_turn");

        State(end.First(e => e.Type == EventTypes.MessagePartUpdated)).ShouldBe("error");
        mapper.Messages()[0].Parts.OfType<ToolUsePart>().Single().State.ShouldBe(ToolUseState.Error);
    }

    [Fact]
    public void the_reply_after_tool_calls_is_a_message_of_its_own_so_it_shows_below_them()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(Chunk("agent_message_chunk", "Let me look."));
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","kind":"read","title":"Read README.md","status":"in_progress"}"""));
        mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed"}"""));
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c2","kind":"execute","title":"ls","status":"in_progress"}"""));
        mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c2","status":"completed"}"""));

        var reply = mapper.Update(Chunk("agent_message_chunk", "Here it is."));
        mapper.Update(Chunk("agent_message_chunk", " Done."));

        reply.Select(e => e.Type).ShouldBe([EventTypes.MessageUpdated, EventTypes.MessageCreated, EventTypes.MessagePartUpdated]);
        reply[0].Payload!.Value.GetProperty("info").GetProperty("time").TryGetProperty("completed", out _).ShouldBeTrue();
        var messages = mapper.Messages();
        messages.Select(m => m.Role).ShouldBe(["assistant", "assistant"]);
        messages[0].Parts.Select(p => p.GetType().Name).ShouldBe(["TextPart", "ToolUsePart", "ToolUsePart"]);
        messages[1].Parts.Select(p => p.GetType().Name).ShouldBe(["TextPart"]);
        messages[1].TextContent.ShouldBe("Here it is. Done.");
        reply[^1].Payload!.Value.GetProperty("part").GetProperty("messageID").GetString().ShouldBe(messages[1].Id);
    }

    [Fact]
    public void thinking_after_a_tool_call_starts_the_next_message_too()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","kind":"read","status":"completed"}"""));

        mapper.Update(Chunk("agent_thought_chunk", "now what"));
        mapper.Update(Chunk("agent_message_chunk", "ok"));

        mapper.Messages().Select(m => m.Parts.Select(p => p.GetType().Name).ToList())
            .ShouldBe([["ToolUsePart"], ["ReasoningPart", "TextPart"]]);
    }

    [Fact]
    public void a_call_still_running_when_the_reply_starts_takes_its_result_in_its_own_message()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(J("""{"sessionUpdate":"tool_call","toolCallId":"c1","kind":"execute","status":"in_progress"}"""));
        var first = mapper.Messages().Single().Id;
        mapper.Update(Chunk("agent_message_chunk", "Still going."));

        var done = mapper.Update(J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed"}"""));
        var end = mapper.EndTurn("end_turn");

        State(done.Single()).ShouldBe("completed");
        done.Single().Payload!.Value.GetProperty("part").GetProperty("messageID").GetString().ShouldBe(first);
        end.Select(e => e.Type).ShouldBe([EventTypes.MessageUpdated, EventTypes.SessionStatus]);
        mapper.Messages().Count.ShouldBe(2);
        mapper.Messages()[0].Parts.OfType<ToolUsePart>().Single().State.ShouldBe(ToolUseState.Completed);
        mapper.Messages()[1].Parts.OfType<ToolUsePart>().ShouldBeEmpty();
    }

    [Fact]
    public void a_fusion_turns_rows_stay_at_its_head_and_finish_there_when_the_answer_streams_below_them()
    {
        var mapper = new AcpMapper("s1");
        mapper.Fusion(J("""{"event":"resolved","pattern":"cascade","model":"model-a","index":0}"""));
        mapper.Fusion(J("""{"event":"phase_started","role":"solver","model":"model-a","index":0}"""));
        mapper.Update(Chunk("agent_message_chunk", "The answer."));

        var pass = mapper.Fusion(J("""{"event":"phase_completed","role":"solver","model":"model-a","index":0,"status":"succeeded","credits":1.5}"""));
        mapper.Fusion(J("""{"event":"completed","pattern":"cascade","status":"completed","totalCredits":1.5,"requests":2}"""));
        mapper.Usage(J("""{"inputTokens":10,"outputTokens":5,"credits":1.5}"""));

        var head = mapper.Messages()[0].Id;
        pass.Single().Payload!.Value.GetProperty("part").GetProperty("messageID").GetString().ShouldBe(head);
        var messages = mapper.Messages();
        messages.Count.ShouldBe(2);
        messages[0].Parts.OfType<ToolUsePart>().Select(r => (r.ToolName, r.State))
            .ShouldBe([(AcpMapper.TurnTool, ToolUseState.Completed), (AcpMapper.PassTool, ToolUseState.Completed)]);
        messages[1].Parts.OfType<ToolUsePart>().ShouldBeEmpty();
        messages[1].TextContent.ShouldBe("The answer.");
    }

    [Fact]
    public void a_plan_update_reports_the_whole_todo_list_and_keeps_it_for_the_session_to_ask_for()
    {
        var mapper = new AcpMapper("s1");

        var events = mapper.Update(J("""{"sessionUpdate":"plan","entries":[{"content":"Pick a spot","priority":"medium","status":"in_progress"},{"content":"Pack","priority":"medium","status":"completed"}]}"""));

        events.Single().Type.ShouldBe(EventTypes.TodosReported);
        events.Single().Payload!.Value.GetProperty("items").GetArrayLength().ShouldBe(2);
        mapper.Todos().Select(t => (t.Content, t.Status)).ShouldBe([("Pick a spot", "in_progress"), ("Pack", "completed")]);
        mapper.Update(J("""{"sessionUpdate":"plan","entries":[]}""")).Single().Type.ShouldBe(EventTypes.TodosReported);
        mapper.Todos().ShouldBeEmpty();
    }

    [Fact]
    public void a_prompt_between_turns_adds_only_the_user_message()
    {
        var mapper = new AcpMapper("s1");

        mapper.UserPrompt("hello").ShouldBeEmpty();

        mapper.Messages().Single().Role.ShouldBe("user");
    }

    [Fact]
    public void a_prompt_with_an_image_keeps_the_image_in_the_user_message()
    {
        var mapper = new AcpMapper("s1");

        mapper.UserPrompt("what is this?", [new HarnessAttachment("image/png", "chart.png", "AAAA")]);

        var parts = mapper.Messages().Single().Parts;
        parts[0].ShouldBeOfType<TextPart>().Text.ShouldBe("what is this?");
        var file = parts[1].ShouldBeOfType<FilePart>();
        file.Mime.ShouldBe("image/png");
        file.Url.ShouldBe("data:image/png;base64,AAAA");
        file.Filename.ShouldBe("chart.png");
    }

    [Fact]
    public void a_usage_update_is_the_contexts_size_with_the_window_and_where_copilot_compacts()
    {
        var mapper = new AcpMapper("s1", model: () => "claude-haiku-4.5");

        var report = ContextEvents.ReadUsage(mapper.Update(J("""{"sessionUpdate":"usage_update","used":16452,"size":136000}""")).Single())!;

        report.Call!.Used.ShouldBe(16452);
        report.Call.TotalOnly.ShouldBeTrue();
        (report.Limit, report.CompactsAt, report.ModelId, report.ProviderId).ShouldBe((136000, 108800, "claude-haiku-4.5", "copilot"));
        mapper.Messages().ShouldBeEmpty();
    }

    [Fact]
    public void a_usage_update_without_its_numbers_says_nothing()
    {
        var mapper = new AcpMapper("s1");

        mapper.Update(J("""{"sessionUpdate":"usage_update"}""")).ShouldBeEmpty();
        ContextEvents.ReadUsage(mapper.Update(J("""{"sessionUpdate":"usage_update","size":200000}""")).Single())!.Call.ShouldBeNull();
    }

    private static JsonElement Compaction(string phase, string trigger = "manual", int? before = null, int? after = null, string? summary = null, string? error = null)
        => JsonSerializer.SerializeToElement(new { sessionId = "s1", phase, trigger, tokensBefore = before, tokensAfter = after, summary, error });

    [Fact]
    public void a_compaction_starting_and_failing_are_context_events_with_no_divider()
    {
        var mapper = new AcpMapper("s1");

        var started = mapper.Compaction(Compaction("started")).Single();
        var failed = mapper.Compaction(Compaction("failed", error: "Too big")).Single();

        ContextEvents.ReadCompaction(started)!.ShouldBeEquivalentTo(new ContextCompactionReport { Phase = "started", Trigger = "manual" });
        ContextEvents.ReadCompaction(failed)!.Error.ShouldBe("Too big");
        mapper.Messages().ShouldBeEmpty();
    }

    [Fact]
    public void a_compaction_ending_adds_a_divider_message_with_its_numbers_and_summary()
    {
        var mapper = new AcpMapper("s1");
        mapper.UserPrompt("hi");

        var events = mapper.Compaction(Compaction("ended", "auto", 9000, 1200, " What we did. "));

        var divider = mapper.Messages()[1];
        divider.Role.ShouldBe("assistant");
        var part = divider.Parts.Single().ShouldBeOfType<CompactionPart>();
        (part.Trigger, part.TokensBefore, part.TokensAfter, part.Summary).ShouldBe(("auto", 9000, 1200, "What we did."));
        events.Select(e => e.Type).ShouldBe([EventTypes.ContextCompaction, EventTypes.MessageUpdated, EventTypes.MessagePartUpdated]);
        events[2].Payload!.Value.GetProperty("part").GetProperty("messageID").GetString().ShouldBe(divider.Id);
    }

    [Fact]
    public void a_compaction_ending_while_the_turns_message_is_open_goes_behind_it_and_the_turn_still_ends_on_its_message()
    {
        var mapper = new AcpMapper("s1");
        mapper.UserPrompt("go");
        mapper.Update(Chunk("agent_message_chunk", "Part one. "));

        mapper.Compaction(Compaction("ended", "auto"));
        mapper.Update(Chunk("agent_message_chunk", "Part two."));
        mapper.Usage(J("""{"inputTokens":100,"outputTokens":20}"""));
        var live = mapper.Messages();
        var end = mapper.EndTurn("end_turn");

        live.Select(m => m.Parts[0].GetType().Name).ShouldBe(["TextPart", "TextPart", "CompactionPart"]);
        var done = mapper.Messages();
        done.Select(m => m.Role).ShouldBe(["user", "assistant", "assistant"]);
        done[1].TextContent.ShouldBe("Part one. Part two.");
        done[1].Parts.OfType<StepFinishPart>().Single().TokensInput.ShouldBe(100);
        done[2].Parts.Single().ShouldBeOfType<CompactionPart>();
        string.CompareOrdinal(done[1].Id, done[2].Id).ShouldBeLessThan(0);
        end.Single(e => e.Type == EventTypes.MessageUpdated).Payload!.Value.GetProperty("info").GetProperty("id").GetString().ShouldBe(done[1].Id);
        end[^1].Type.ShouldBe(EventTypes.SessionStatus);
    }

    [Fact]
    public void a_compaction_ending_after_the_turn_ended_follows_the_turns_message()
    {
        var mapper = new AcpMapper("s1");
        mapper.UserPrompt("go");
        mapper.Update(Chunk("agent_message_chunk", "Done."));
        mapper.EndTurn("end_turn");

        mapper.Compaction(Compaction("ended", "auto"));

        mapper.Messages().Select(m => m.Parts[0].GetType().Name).ShouldBe(["TextPart", "TextPart", "CompactionPart"]);
    }

    [Fact]
    public void a_replayed_conversation_keeps_the_divider_in_its_place()
    {
        var mapper = new AcpMapper("s1");
        mapper.Update(Chunk("user_message_chunk", "first"));
        mapper.Update(Chunk("agent_message_chunk", "answer"));
        mapper.Compaction(Compaction("ended", "manual"));
        mapper.Update(Chunk("user_message_chunk", "second"));
        mapper.Update(Chunk("agent_message_chunk", "more"));

        mapper.Messages().Select(m => m.Parts[0] is CompactionPart ? "divider" : m.TextContent).ShouldBe(["first", "answer", "divider", "second", "more"]);
    }
}
