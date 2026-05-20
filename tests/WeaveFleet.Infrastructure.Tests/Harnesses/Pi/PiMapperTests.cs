using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Pi;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Pi;

public sealed class PiMapperTests
{
    private const string SessionId = "session-1";

    [Fact]
    public void agent_and_turn_events_map_busy_completed_messages_step_finish_and_idle_statuses()
    {
        var mapper = new PiMapper(SessionId, "loom");
        var message = AssistantMessage(1000, new PiTextContent { Text = "done" });

        var agentStart = mapper.Map(new PiAgentStartEvent());
        var turnStart = mapper.Map(new PiTurnStartEvent());
        var turnEnd = mapper.Map(new PiTurnEndEvent { Message = message });
        var agentEnd = mapper.Map(new PiAgentEndEvent { Messages = new List<PiMessage> { message } });

        agentStart.Single().Type.ShouldBe(EventTypes.SessionStatus);
        StatusType(agentStart.Single()).ShouldBe("busy");
        StatusAgent(agentStart.Single()).ShouldBe("loom");
        turnStart.Single().Type.ShouldBe(EventTypes.SessionStatus);
        StatusType(turnStart.Single()).ShouldBe("busy");

        turnEnd.Select(evt => evt.Type).ShouldBe(new List<string>
        {
            EventTypes.MessageUpdated,
            EventTypes.MessagePartUpdated,
            EventTypes.SessionStatus,
        });
        MessageId(turnEnd[0]).ShouldBe("pi-session-1-assistant-1000");
        LifecycleCompleted(turnEnd[0]).ShouldNotBeNull();
        Part(turnEnd[1]).GetProperty("type").GetString().ShouldBe("step-finish");
        StatusType(turnEnd[2]).ShouldBe("idle");
        StatusMessageId(turnEnd[2]).ShouldBe("pi-session-1-assistant-1000");
        StatusReason(turnEnd[2]).ShouldBe("end_turn");
        StatusCompletedAt(turnEnd[2]).ShouldNotBeNull();

        agentEnd.Select(evt => evt.Type).ShouldBe(new List<string>
        {
            EventTypes.MessageUpdated,
            EventTypes.SessionStatus,
        });
        StatusType(agentEnd[1]).ShouldBe("idle");
    }

    [Fact]
    public void message_start_update_end_lifecycle_uses_stable_ids_and_materializes_parts()
    {
        var mapper = new PiMapper(SessionId, "thread");
        var started = AssistantMessage(2000, new PiTextContent { Text = "" });
        var updated = AssistantMessage(
            2000,
            new PiTextContent { Text = "hello" },
            new PiThinkingContent { Thinking = "because" },
            new PiToolCallContent { Id = "call-1", Name = "bash", Arguments = Json("{\"cmd\":\"pwd\"}") });

        var startEvents = mapper.Map(new PiMessageStartEvent { Message = started });
        var updateEvents = mapper.Map(new PiMessageUpdateEvent { Message = updated });
        var endEvents = mapper.Map(new PiMessageEndEvent { Message = updated });

        startEvents.Single().Type.ShouldBe(EventTypes.MessageCreated);
        MessageId(startEvents.Single()).ShouldBe("pi-session-1-assistant-2000");
        LifecycleCompleted(startEvents.Single()).ShouldBeNull();
        LifecycleParts(startEvents.Single()).Count.ShouldBe(1);

        updateEvents.Single().Type.ShouldBe(EventTypes.MessageUpdated);
        MessageId(updateEvents.Single()).ShouldBe("pi-session-1-assistant-2000");
        var parts = LifecycleParts(updateEvents.Single());
        parts.Count.ShouldBe(3);
        parts[0].GetProperty("id").GetString().ShouldBe("pi-session-1-assistant-2000-content-0");
        parts[0].GetProperty("text").GetString().ShouldBe("hello");
        parts[1].GetProperty("type").GetString().ShouldBe("reasoning");
        parts[1].GetProperty("text").GetString().ShouldBe("because");
        parts[2].GetProperty("type").GetString().ShouldBe("tool");
        parts[2].GetProperty("tool").GetString().ShouldBe("bash");
        parts[2].GetProperty("callID").GetString().ShouldBe("call-1");
        parts[2].GetProperty("state").GetProperty("input").GetProperty("cmd").GetString().ShouldBe("pwd");

        endEvents.Select(evt => evt.Type).ShouldBe(new List<string>
        {
            EventTypes.MessageUpdated,
            EventTypes.MessagePartUpdated,
        });
        MessageId(endEvents[0]).ShouldBe("pi-session-1-assistant-2000");
        LifecycleCompleted(endEvents[0]).ShouldNotBeNull();
        Part(endEvents[1]).GetProperty("type").GetString().ShouldBe("step-finish");
    }

    [Fact]
    public void text_start_delta_empty_delta_and_end_map_part_updates_and_streamed_delta()
    {
        var mapper = new PiMapper(SessionId);
        var partial = AssistantMessage(3000, new PiTextContent { Text = "full text" });

        mapper.Map(new PiMessageStartEvent { Message = AssistantMessage(3000, new PiTextContent { Text = string.Empty }) });
        var start = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiTextStartEvent { ContentIndex = 0, Partial = partial } });
        var delta = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiTextDeltaEvent { ContentIndex = 0, Delta = "hi" } });
        var emptyDelta = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiTextDeltaEvent { ContentIndex = 0, Delta = string.Empty, Partial = partial } });
        var end = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiTextEndEvent { ContentIndex = 0, Content = "final" } });

        start.Single().Type.ShouldBe(EventTypes.MessagePartUpdated);
        Part(start.Single()).GetProperty("text").GetString().ShouldBe(string.Empty);
        delta.Single().Type.ShouldBe(EventTypes.MessagePartDelta);
        Payload(delta.Single()).GetProperty("messageID").GetString().ShouldBe("pi-session-1-assistant-3000");
        Payload(delta.Single()).GetProperty("partID").GetString().ShouldBe("pi-session-1-assistant-3000-content-0");
        Payload(delta.Single()).GetProperty("field").GetString().ShouldBe("text");
        Payload(delta.Single()).GetProperty("delta").GetString().ShouldBe("hi");
        emptyDelta.Single().Type.ShouldBe(EventTypes.MessagePartUpdated);
        Part(emptyDelta.Single()).GetProperty("text").GetString().ShouldBe("full text");
        end.Single().Type.ShouldBe(EventTypes.MessagePartUpdated);
        Part(end.Single()).GetProperty("text").GetString().ShouldBe("final");
    }

    [Fact]
    public void thinking_start_delta_empty_delta_and_end_map_reasoning_part_updates()
    {
        var mapper = new PiMapper(SessionId);
        var partial = AssistantMessage(3100, new PiThinkingContent { Thinking = "full reasoning" });

        mapper.Map(new PiMessageStartEvent { Message = AssistantMessage(3100, new PiThinkingContent { Thinking = string.Empty }) });
        var start = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiThinkingStartEvent { ContentIndex = 0 } });
        var delta = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiThinkingDeltaEvent { ContentIndex = 0, Delta = "think" } });
        var emptyDelta = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiThinkingDeltaEvent { ContentIndex = 0, Delta = string.Empty, Partial = partial } });
        var end = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiThinkingEndEvent { ContentIndex = 0, Content = "final reasoning" } });

        Part(start.Single()).GetProperty("type").GetString().ShouldBe("reasoning");
        Part(start.Single()).GetProperty("text").GetString().ShouldBe(string.Empty);
        Part(delta.Single()).GetProperty("text").GetString().ShouldBe("think");
        Part(emptyDelta.Single()).GetProperty("text").GetString().ShouldBe("full reasoning");
        Part(end.Single()).GetProperty("text").GetString().ShouldBe("final reasoning");
    }

    [Fact]
    public void toolcall_start_delta_end_maps_pending_tool_parts_and_tracks_execution_by_multiple_ids()
    {
        var mapper = new PiMapper(SessionId);

        mapper.Map(new PiMessageStartEvent { Message = AssistantMessage(4000, new PiTextContent { Text = string.Empty }) });
        var toolCallStart = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiToolCallStartEvent { ContentIndex = 1 } });
        var toolCallDelta = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiToolCallDeltaEvent { ContentIndex = 1, Delta = "{\"path\":\"/tmp\"}" } });
        var toolCallEnd = mapper.Map(new PiMessageUpdateEvent
        {
            AssistantMessageEvent = new PiToolCallEndEvent
            {
                ContentIndex = 1,
                ToolCall = new PiToolCallContent { Id = "call-a", Name = "read", Arguments = Json("{\"path\":\"/tmp\"}") },
            },
        });
        var callAStart = mapper.Map(new PiToolExecutionStartEvent { ToolCallId = "call-a", ToolName = "read", Args = Json("{\"path\":\"/tmp\"}") });
        var callBEnd = mapper.Map(new PiToolExecutionEndEvent
        {
            ToolCallId = "call-b",
            ToolName = "write",
            Result = new PiToolResult { Details = Json("{\"ok\":true}") },
        });

        ToolStateStatus(toolCallStart.Single()).ShouldBe("pending");
        Part(toolCallStart.Single()).GetProperty("callID").GetString().ShouldBe("pi-session-1-assistant-4000-content-1");
        ToolStateStatus(toolCallDelta.Single()).ShouldBe("pending");
        ToolState(toolCallDelta.Single()).GetProperty("input").GetProperty("path").GetString().ShouldBe("/tmp");
        Part(toolCallEnd.Single()).GetProperty("callID").GetString().ShouldBe("call-a");
        Part(toolCallEnd.Single()).GetProperty("tool").GetString().ShouldBe("read");
        ToolStateStatus(callAStart.Single()).ShouldBe("running");
        Part(callAStart.Single()).GetProperty("id").GetString().ShouldBe("pi-session-1-assistant-4000-tool-call-a");
        ToolStateStatus(callBEnd.Single()).ShouldBe("completed");
        Part(callBEnd.Single()).GetProperty("callID").GetString().ShouldBe("call-b");
        Part(callBEnd.Single()).GetProperty("id").GetString().ShouldBe("pi-session-1-assistant-4000-tool-call-b");
        ToolState(callBEnd.Single()).GetProperty("output").GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void tool_execution_update_and_error_end_preserve_input_and_map_text_results()
    {
        var mapper = new PiMapper(SessionId);

        var start = mapper.Map(new PiToolExecutionStartEvent { ToolCallId = "call-error", ToolName = "bash", Args = Json("{\"cmd\":\"bad\"}") });
        var update = mapper.Map(new PiToolExecutionUpdateEvent
        {
            ToolCallId = "call-error",
            ToolName = "bash",
            PartialResult = new PiToolResult { Content = new List<PiContentBlock> { new PiTextContent { Text = "partial" } } },
        });
        var end = mapper.Map(new PiToolExecutionEndEvent
        {
            ToolCallId = "call-error",
            ToolName = "bash",
            Result = new PiToolResult { Content = new List<PiContentBlock> { new PiTextContent { Text = "failed" } } },
            IsError = true,
        });

        ToolStateStatus(start.Single()).ShouldBe("running");
        ToolState(start.Single()).GetProperty("input").GetProperty("cmd").GetString().ShouldBe("bad");
        ToolStateStatus(update.Single()).ShouldBe("running");
        ToolStateStatus(end.Single()).ShouldBe("error");
        ToolState(end.Single()).GetProperty("input").GetProperty("cmd").GetString().ShouldBe("bad");
        ToolState(end.Single()).GetProperty("output").GetString().ShouldBe("failed");
    }

    [Fact]
    public void aborted_assistant_message_emits_message_update_step_finish_and_session_error()
    {
        var mapper = new PiMapper(SessionId);
        var message = AssistantMessage("aborted", "User aborted.", 5000, new PiTextContent { Text = "partial" });

        var events = mapper.Map(new PiMessageEndEvent { Message = message });

        events.Select(evt => evt.Type).ShouldBe(new List<string>
        {
            EventTypes.MessageUpdated,
            EventTypes.MessagePartUpdated,
            EventTypes.SessionError,
        });
        LifecycleCompleted(events[0]).ShouldNotBeNull();
        Part(events[1]).GetProperty("type").GetString().ShouldBe("step-finish");
        Payload(events[2]).GetProperty("message").GetString().ShouldBe("User aborted.");
    }

    [Fact]
    public void compaction_retry_session_state_response_and_protocol_errors_map_expected_events()
    {
        var mapper = new PiMapper(SessionId);

        var compactionStart = mapper.Map(new PiCompactionStartEvent { Message = "compacting" });
        var compactionEnd = mapper.Map(new PiCompactionEndEvent { Message = "done", Success = true });
        var retryStart = mapper.Map(new PiAutoRetryStartEvent { Reason = "rate_limit" });
        var retryEnd = mapper.Map(new PiAutoRetryEndEvent { Success = false });
        var switched = mapper.Map(new PiSessionSwitchedEvent { SessionFile = "pi.jsonl", SessionId = "pi-session" });
        var state = mapper.Map(new PiStateUpdateEvent { State = new PiState { SessionFile = "state.jsonl", SessionId = "state-session", IsStreaming = true, IsCompacting = true, PendingMessageCount = 2 } });
        var getState = mapper.Map(new PiResponseEvent { Command = "get_state", Success = true, Data = Json("{\"sessionId\":\"raw\"}") });
        var failedResponse = mapper.Map(new PiResponseEvent { Command = "prompt", Success = false, Error = "failed" });
        var error = mapper.Map(new PiErrorEvent { Error = "raw error" });
        var protocolError = mapper.Map(new PiProtocolErrorEvent { Kind = "unknown_event", Message = "bad event" });

        StatusType(compactionStart.Single()).ShouldBe("working");
        StatusActivity(compactionStart.Single()).ShouldBe("compaction_start");
        compactionEnd.Single().Type.ShouldBe(EventTypes.SessionCompacted);
        Payload(compactionEnd.Single()).GetProperty("message").GetString().ShouldBe("done");
        Payload(compactionEnd.Single()).GetProperty("success").GetBoolean().ShouldBeTrue();
        StatusActivity(retryStart.Single()).ShouldBe("auto_retry_start");
        StatusDetail(retryStart.Single()).ShouldBe("rate_limit");
        StatusType(retryEnd.Single()).ShouldBe("busy");
        StatusActivity(retryEnd.Single()).ShouldBe("auto_retry_end");
        StatusDetail(retryEnd.Single()).ShouldBe("False");
        switched.Single().Type.ShouldBe(EventTypes.SessionUpdated);
        Payload(switched.Single()).GetProperty("sessionFile").GetString().ShouldBe("pi.jsonl");
        state.Single().Type.ShouldBe(EventTypes.SessionUpdated);
        Payload(state.Single()).GetProperty("isStreaming").GetBoolean().ShouldBeTrue();
        Payload(state.Single()).GetProperty("pendingMessageCount").GetInt32().ShouldBe(2);
        getState.Single().Type.ShouldBe(EventTypes.SessionUpdated);
        Payload(getState.Single()).GetProperty("sessionId").GetString().ShouldBe("raw");
        failedResponse.Single().Type.ShouldBe(EventTypes.Error);
        Payload(failedResponse.Single()).GetProperty("message").GetString().ShouldBe("failed");
        Payload(error.Single()).GetProperty("message").GetString().ShouldBe("raw error");
        Payload(protocolError.Single()).GetProperty("message").GetString().ShouldBe("bad event");
    }

    [Fact]
    public void queue_log_successful_non_state_response_suppressed_message_and_unknown_events_are_ignored()
    {
        var mapper = new PiMapper(SessionId);

        mapper.Map(new PiQueueUpdateEvent { PendingMessageCount = 1 }).ShouldBeEmpty();
        mapper.Map(new PiLogEvent { Level = "info", Message = "hello" }).ShouldBeEmpty();
        mapper.Map(new PiResponseEvent { Command = "prompt", Success = true }).ShouldBeEmpty();
        mapper.Map(new PiMessageStartEvent { Message = new PiMessage { Role = "" } }).ShouldBeEmpty();
        mapper.Map(new PiEvent()).ShouldBeEmpty();
    }

    [Fact]
    public void idle_and_out_of_order_lifecycle_fallback_create_stable_synthetic_ids()
    {
        var mapper = new PiMapper(SessionId);

        var textDelta = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiTextDeltaEvent { ContentIndex = 0, Delta = "orphan" } });
        var textEnd = mapper.Map(new PiMessageUpdateEvent { AssistantMessageEvent = new PiTextEndEvent { ContentIndex = 0 } });
        var toolStart = mapper.Map(new PiToolExecutionStartEvent { ToolCallId = "late-call", ToolName = "bash" });
        var idle = mapper.Map(new PiIdleEvent());

        Payload(textDelta.Single()).GetProperty("messageID").GetString().ShouldBe("pi-session-1-assistant-1");
        Payload(textDelta.Single()).GetProperty("partID").GetString().ShouldBe("pi-session-1-assistant-1-content-0");
        Part(textEnd.Single()).GetProperty("messageID").GetString().ShouldBe("pi-session-1-assistant-1");
        Part(textEnd.Single()).GetProperty("text").GetString().ShouldBe("orphan");
        Part(toolStart.Single()).GetProperty("messageID").GetString().ShouldBe("pi-session-1-assistant-1");
        Part(toolStart.Single()).GetProperty("id").GetString().ShouldBe("pi-session-1-assistant-1-tool-late-call");
        StatusType(idle.Single()).ShouldBe("idle");
        StatusMessageId(idle.Single()).ShouldBe("pi-session-1-assistant-1");
    }

    private static PiMessage AssistantMessage(long timestamp, params PiContentBlock[] content)
        => new()
        {
            Role = "assistant",
            Content = content,
            Timestamp = timestamp,
            StopReason = "end_turn",
            ResponseModel = "model-1",
            Usage = new PiUsage
            {
                Input = 10,
                Output = 20,
                Cost = new PiCost { Total = 0.42 },
            },
        };

    private static PiMessage AssistantMessage(string stopReason, string errorMessage, long timestamp, params PiContentBlock[] content)
        => new()
        {
            Role = "assistant",
            Content = content,
            Timestamp = timestamp,
            StopReason = stopReason,
            ErrorMessage = errorMessage,
        };

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static JsonElement Payload(HarnessEvent evt)
    {
        evt.Payload.HasValue.ShouldBeTrue();
        return evt.Payload.Value;
    }

    private static JsonElement Part(HarnessEvent evt)
        => Payload(evt).GetProperty("part");

    private static JsonElement ToolState(HarnessEvent evt)
        => Part(evt).GetProperty("state");

    private static string? ToolStateStatus(HarnessEvent evt)
        => ToolState(evt).GetProperty("status").GetString();

    private static JsonElement Status(HarnessEvent evt)
        => Payload(evt).GetProperty("status");

    private static string? StatusType(HarnessEvent evt)
        => Status(evt).GetProperty("type").GetString();

    private static string? StatusActivity(HarnessEvent evt)
        => Status(evt).GetProperty("activity").GetString();

    private static string? StatusDetail(HarnessEvent evt)
        => Status(evt).GetProperty("detail").GetString();

    private static string? StatusAgent(HarnessEvent evt)
        => Status(evt).GetProperty("agent").GetString();

    private static string? StatusReason(HarnessEvent evt)
        => Status(evt).GetProperty("reason").GetString();

    private static string? StatusMessageId(HarnessEvent evt)
        => Status(evt).TryGetProperty("messageID", out var messageId) ? messageId.GetString() : null;

    private static long? StatusCompletedAt(HarnessEvent evt)
        => Status(evt).TryGetProperty("completedAt", out var completedAt) ? completedAt.GetInt64() : null;

    private static string? MessageId(HarnessEvent evt)
        => Payload(evt).GetProperty("info").GetProperty("id").GetString();

    private static long? LifecycleCompleted(HarnessEvent evt)
    {
        var time = Payload(evt).GetProperty("info").GetProperty("time");
        return time.TryGetProperty("completed", out var completed) ? completed.GetInt64() : null;
    }

    private static List<JsonElement> LifecycleParts(HarnessEvent evt)
        => Payload(evt).GetProperty("parts").EnumerateArray().ToList();
}
