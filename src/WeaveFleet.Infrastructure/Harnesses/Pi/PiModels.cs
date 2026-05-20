using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary>Shared <see cref="JsonSerializerOptions"/> for Pi RPC JSONL serialization.</summary>
internal static class PiJsonOptions
{
    internal static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// ---------------------------------------------------------------------------
// Commands — stdin, discriminated by "type"
// ---------------------------------------------------------------------------

/// <summary>Base envelope for all Pi RPC commands sent to stdin.</summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(PiPromptCommand), "prompt")]
[JsonDerivedType(typeof(PiSteerCommand), "steer")]
[JsonDerivedType(typeof(PiFollowUpCommand), "follow_up")]
[JsonDerivedType(typeof(PiAbortCommand), "abort")]
[JsonDerivedType(typeof(PiGetStateCommand), "get_state")]
[JsonDerivedType(typeof(PiGetMessagesCommand), "get_messages")]
[JsonDerivedType(typeof(PiSetModelCommand), "set_model")]
[JsonDerivedType(typeof(PiSetThinkingLevelCommand), "set_thinking_level")]
[JsonDerivedType(typeof(PiCompactCommand), "compact")]
[JsonDerivedType(typeof(PiBashCommand), "bash")]
[JsonDerivedType(typeof(PiNewSessionCommand), "new_session")]
[JsonDerivedType(typeof(PiForkCommand), "fork")]
[JsonDerivedType(typeof(PiCloneCommand), "clone")]
[JsonDerivedType(typeof(PiSwitchSessionCommand), "switch_session")]
internal record PiCommand
{
    /// <summary>Optional command correlation identifier echoed by response events.</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }
}

/// <summary>Send a user prompt to the current session.</summary>
internal sealed record PiPromptCommand : PiCommand
{
    [JsonPropertyName("message")] public required string Message { get; init; }
}

/// <summary>Steer an in-flight turn.</summary>
internal sealed record PiSteerCommand : PiCommand
{
    [JsonPropertyName("message")] public required string Message { get; init; }
}

/// <summary>Queue or send a follow-up message.</summary>
internal sealed record PiFollowUpCommand : PiCommand
{
    [JsonPropertyName("message")] public required string Message { get; init; }
}

/// <summary>Abort the active turn.</summary>
internal sealed record PiAbortCommand : PiCommand;

/// <summary>Request current Pi session state.</summary>
internal sealed record PiGetStateCommand : PiCommand;

/// <summary>Request current Pi conversation messages.</summary>
internal sealed record PiGetMessagesCommand : PiCommand;

/// <summary>Response payload returned by <c>get_messages</c>.</summary>
internal sealed record PiGetMessagesResponse
{
    [JsonPropertyName("messages")] public IReadOnlyList<PiMessage> Messages { get; init; } = [];
    [JsonPropertyName("hasMore")] public bool? HasMore { get; init; }
}

/// <summary>Serialized Fleet resume token for Pi sessions.</summary>
internal sealed record PiResumeToken
{
    [JsonPropertyName("sessionFile")] public string? SessionFile { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
}

/// <summary>Change the active model.</summary>
internal sealed record PiSetModelCommand : PiCommand
{
    [JsonPropertyName("provider")] public string? Provider { get; init; }
    [JsonPropertyName("model")] public required string Model { get; init; }
}

/// <summary>Change the active reasoning/thinking level.</summary>
internal sealed record PiSetThinkingLevelCommand : PiCommand
{
    [JsonPropertyName("thinkingLevel")] public required string ThinkingLevel { get; init; }
}

/// <summary>Compact the current conversation context.</summary>
internal sealed record PiCompactCommand : PiCommand;

/// <summary>Execute a shell command via Pi RPC.</summary>
internal sealed record PiBashCommand : PiCommand
{
    [JsonPropertyName("command")] public required string Command { get; init; }
}

/// <summary>Start a new Pi session.</summary>
internal sealed record PiNewSessionCommand : PiCommand;

/// <summary>Fork the current Pi session.</summary>
internal sealed record PiForkCommand : PiCommand;

/// <summary>Clone the current Pi session.</summary>
internal sealed record PiCloneCommand : PiCommand;

/// <summary>Switch to an existing Pi session file or session identifier.</summary>
internal sealed record PiSwitchSessionCommand : PiCommand
{
    [JsonPropertyName("sessionPath")] public required string SessionPath { get; init; }
}

// ---------------------------------------------------------------------------
// Events — stdout, discriminated by "type"
// ---------------------------------------------------------------------------

/// <summary>Base type for all Pi RPC events read from stdout.</summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(PiResponseEvent), "response")]
[JsonDerivedType(typeof(PiAgentStartEvent), "agent_start")]
[JsonDerivedType(typeof(PiAgentEndEvent), "agent_end")]
[JsonDerivedType(typeof(PiTurnStartEvent), "turn_start")]
[JsonDerivedType(typeof(PiTurnEndEvent), "turn_end")]
[JsonDerivedType(typeof(PiMessageStartEvent), "message_start")]
[JsonDerivedType(typeof(PiMessageUpdateEvent), "message_update")]
[JsonDerivedType(typeof(PiMessageEndEvent), "message_end")]
[JsonDerivedType(typeof(PiToolExecutionStartEvent), "tool_execution_start")]
[JsonDerivedType(typeof(PiToolExecutionUpdateEvent), "tool_execution_update")]
[JsonDerivedType(typeof(PiToolExecutionEndEvent), "tool_execution_end")]
[JsonDerivedType(typeof(PiCompactionStartEvent), "compaction_start")]
[JsonDerivedType(typeof(PiCompactionEndEvent), "compaction_end")]
[JsonDerivedType(typeof(PiAutoRetryStartEvent), "auto_retry_start")]
[JsonDerivedType(typeof(PiAutoRetryEndEvent), "auto_retry_end")]
[JsonDerivedType(typeof(PiQueueUpdateEvent), "queue_update")]
[JsonDerivedType(typeof(PiIdleEvent), "idle")]
[JsonDerivedType(typeof(PiErrorEvent), "error")]
[JsonDerivedType(typeof(PiLogEvent), "log")]
[JsonDerivedType(typeof(PiSessionSwitchedEvent), "session_switched")]
[JsonDerivedType(typeof(PiStateUpdateEvent), "state_update")]
internal record PiEvent;

/// <summary>Command response event, optionally correlated by id.</summary>
internal sealed record PiResponseEvent : PiEvent
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("command")] public required string Command { get; init; }
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("data")] public JsonElement? Data { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>Agent execution started.</summary>
internal sealed record PiAgentStartEvent : PiEvent;

/// <summary>Agent execution ended with accumulated messages.</summary>
internal sealed record PiAgentEndEvent : PiEvent
{
    [JsonPropertyName("messages")] public IReadOnlyList<PiMessage> Messages { get; init; } = [];
}

/// <summary>Model turn started.</summary>
internal sealed record PiTurnStartEvent : PiEvent;

/// <summary>Model turn ended.</summary>
internal sealed record PiTurnEndEvent : PiEvent
{
    [JsonPropertyName("message")] public PiMessage? Message { get; init; }
    [JsonPropertyName("toolResults")] public IReadOnlyList<PiToolResult> ToolResults { get; init; } = [];
}

/// <summary>A message has started.</summary>
internal sealed record PiMessageStartEvent : PiEvent
{
    [JsonPropertyName("message")] public required PiMessage Message { get; init; }
}

/// <summary>A message has changed, usually with an assistant content-block event.</summary>
internal sealed record PiMessageUpdateEvent : PiEvent
{
    [JsonPropertyName("assistantMessageEvent")] public PiAssistantMessageEvent? AssistantMessageEvent { get; init; }
    [JsonPropertyName("message")] public PiMessage? Message { get; init; }
}

/// <summary>A message has ended.</summary>
internal sealed record PiMessageEndEvent : PiEvent
{
    [JsonPropertyName("message")] public required PiMessage Message { get; init; }
}

/// <summary>A tool execution has started.</summary>
internal sealed record PiToolExecutionStartEvent : PiEvent
{
    [JsonPropertyName("toolCallId")] public required string ToolCallId { get; init; }
    [JsonPropertyName("toolName")] public required string ToolName { get; init; }
    [JsonPropertyName("args")] public JsonElement? Args { get; init; }
}

/// <summary>A tool execution emitted a partial result.</summary>
internal sealed record PiToolExecutionUpdateEvent : PiEvent
{
    [JsonPropertyName("toolCallId")] public required string ToolCallId { get; init; }
    [JsonPropertyName("toolName")] public required string ToolName { get; init; }
    [JsonPropertyName("args")] public JsonElement? Args { get; init; }
    [JsonPropertyName("partialResult")] public PiToolResult? PartialResult { get; init; }
}

/// <summary>A tool execution has ended.</summary>
internal sealed record PiToolExecutionEndEvent : PiEvent
{
    [JsonPropertyName("toolCallId")] public required string ToolCallId { get; init; }
    [JsonPropertyName("toolName")] public required string ToolName { get; init; }
    [JsonPropertyName("result")] public PiToolResult? Result { get; init; }
    [JsonPropertyName("isError")] public bool IsError { get; init; }
}

/// <summary>Context compaction started.</summary>
internal sealed record PiCompactionStartEvent : PiEvent
{
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>Context compaction ended.</summary>
internal sealed record PiCompactionEndEvent : PiEvent
{
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("success")] public bool? Success { get; init; }
}

/// <summary>Automatic retry started.</summary>
internal sealed record PiAutoRetryStartEvent : PiEvent
{
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("attempt")] public int? Attempt { get; init; }
    [JsonPropertyName("delayMs")] public int? DelayMs { get; init; }
}

/// <summary>Automatic retry ended.</summary>
internal sealed record PiAutoRetryEndEvent : PiEvent
{
    [JsonPropertyName("success")] public bool? Success { get; init; }
    [JsonPropertyName("attempt")] public int? Attempt { get; init; }
}

/// <summary>Pending prompt queue state changed.</summary>
internal sealed record PiQueueUpdateEvent : PiEvent
{
    [JsonPropertyName("pendingMessageCount")] public int? PendingMessageCount { get; init; }
}

/// <summary>Pi reported that the session is idle.</summary>
internal sealed record PiIdleEvent : PiEvent;

/// <summary>Pi emitted a protocol/runtime error event.</summary>
internal sealed record PiErrorEvent : PiEvent
{
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>Pi emitted an informational log event.</summary>
internal sealed record PiLogEvent : PiEvent
{
    [JsonPropertyName("level")] public string? Level { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>Pi switched the active session.</summary>
internal sealed record PiSessionSwitchedEvent : PiEvent
{
    [JsonPropertyName("sessionFile")] public string? SessionFile { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
}

/// <summary>Pi emitted a state update.</summary>
internal sealed record PiStateUpdateEvent : PiEvent
{
    [JsonPropertyName("state")] public PiState? State { get; init; }
}

// ---------------------------------------------------------------------------
// Assistant message events — nested under message_update.assistantMessageEvent
// ---------------------------------------------------------------------------

/// <summary>Base type for nested assistant message events.</summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(PiThinkingStartEvent), "thinking_start")]
[JsonDerivedType(typeof(PiThinkingDeltaEvent), "thinking_delta")]
[JsonDerivedType(typeof(PiThinkingEndEvent), "thinking_end")]
[JsonDerivedType(typeof(PiTextStartEvent), "text_start")]
[JsonDerivedType(typeof(PiTextDeltaEvent), "text_delta")]
[JsonDerivedType(typeof(PiTextEndEvent), "text_end")]
[JsonDerivedType(typeof(PiToolCallStartEvent), "toolcall_start")]
[JsonDerivedType(typeof(PiToolCallDeltaEvent), "toolcall_delta")]
[JsonDerivedType(typeof(PiToolCallEndEvent), "toolcall_end")]
internal record PiAssistantMessageEvent
{
    [JsonPropertyName("contentIndex")] public int ContentIndex { get; init; }
    [JsonPropertyName("partial")] public PiMessage? Partial { get; init; }
}

/// <summary>Thinking content block started.</summary>
internal sealed record PiThinkingStartEvent : PiAssistantMessageEvent;

/// <summary>Thinking content block streamed text.</summary>
internal sealed record PiThinkingDeltaEvent : PiAssistantMessageEvent
{
    [JsonPropertyName("delta")] public string? Delta { get; init; }
}

/// <summary>Thinking content block ended.</summary>
internal sealed record PiThinkingEndEvent : PiAssistantMessageEvent
{
    [JsonPropertyName("content")] public string? Content { get; init; }
}

/// <summary>Text content block started.</summary>
internal sealed record PiTextStartEvent : PiAssistantMessageEvent;

/// <summary>Text content block streamed text.</summary>
internal sealed record PiTextDeltaEvent : PiAssistantMessageEvent
{
    [JsonPropertyName("delta")] public string? Delta { get; init; }
}

/// <summary>Text content block ended.</summary>
internal sealed record PiTextEndEvent : PiAssistantMessageEvent
{
    [JsonPropertyName("content")] public string? Content { get; init; }
}

/// <summary>Tool-call content block started.</summary>
internal sealed record PiToolCallStartEvent : PiAssistantMessageEvent;

/// <summary>Tool-call content block streamed arguments.</summary>
internal sealed record PiToolCallDeltaEvent : PiAssistantMessageEvent
{
    [JsonPropertyName("delta")] public string? Delta { get; init; }
}

/// <summary>Tool-call content block ended.</summary>
internal sealed record PiToolCallEndEvent : PiAssistantMessageEvent
{
    [JsonPropertyName("toolCall")] public PiToolCallContent? ToolCall { get; init; }
}

// ---------------------------------------------------------------------------
// Message payloads
// ---------------------------------------------------------------------------

/// <summary>Pi message object shared by message lifecycle events and state responses.</summary>
internal sealed record PiMessage
{
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonPropertyName("content")] public IReadOnlyList<PiContentBlock> Content { get; init; } = [];
    [JsonPropertyName("api")] public string? Api { get; init; }
    [JsonPropertyName("provider")] public string? Provider { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("usage")] public PiUsage? Usage { get; init; }
    [JsonPropertyName("stopReason")] public string? StopReason { get; init; }
    [JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
    [JsonPropertyName("responseId")] public string? ResponseId { get; init; }
    [JsonPropertyName("responseModel")] public string? ResponseModel { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    [JsonPropertyName("toolCallId")] public string? ToolCallId { get; init; }
    [JsonPropertyName("toolName")] public string? ToolName { get; init; }
    [JsonPropertyName("isError")] public bool? IsError { get; init; }
}

/// <summary>Base type for Pi message/tool-result content blocks.</summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(PiTextContent), "text")]
[JsonDerivedType(typeof(PiThinkingContent), "thinking")]
[JsonDerivedType(typeof(PiToolCallContent), "toolCall")]
internal record PiContentBlock;

/// <summary>Plain text content.</summary>
internal sealed record PiTextContent : PiContentBlock
{
    [JsonPropertyName("text")] public string? Text { get; init; }
}

/// <summary>Model reasoning/thinking content.</summary>
internal sealed record PiThinkingContent : PiContentBlock
{
    [JsonPropertyName("thinking")] public string? Thinking { get; init; }
    [JsonPropertyName("thinkingSignature")] public string? ThinkingSignature { get; init; }
}

/// <summary>Model-requested tool invocation content.</summary>
internal sealed record PiToolCallContent : PiContentBlock
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("arguments")] public JsonElement? Arguments { get; init; }
    [JsonPropertyName("partialArgs")] public string? PartialArgs { get; init; }
    [JsonPropertyName("streamIndex")] public int? StreamIndex { get; init; }
}

/// <summary>Tool execution result payload.</summary>
internal sealed record PiToolResult
{
    [JsonPropertyName("content")] public IReadOnlyList<PiContentBlock> Content { get; init; } = [];
    [JsonPropertyName("details")] public JsonElement? Details { get; init; }
}

// ---------------------------------------------------------------------------
// State / usage / model payloads
// ---------------------------------------------------------------------------

/// <summary>Pi session state returned by get_state responses.</summary>
internal sealed record PiState
{
    [JsonPropertyName("model")] public PiModelInfo? Model { get; init; }
    [JsonPropertyName("thinkingLevel")] public string? ThinkingLevel { get; init; }
    [JsonPropertyName("isStreaming")] public bool IsStreaming { get; init; }
    [JsonPropertyName("isCompacting")] public bool IsCompacting { get; init; }
    [JsonPropertyName("steeringMode")] public string? SteeringMode { get; init; }
    [JsonPropertyName("followUpMode")] public string? FollowUpMode { get; init; }
    [JsonPropertyName("sessionFile")] public string? SessionFile { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("autoCompactionEnabled")] public bool AutoCompactionEnabled { get; init; }
    [JsonPropertyName("messageCount")] public int MessageCount { get; init; }
    [JsonPropertyName("pendingMessageCount")] public int PendingMessageCount { get; init; }
}

/// <summary>Pi model metadata.</summary>
internal sealed record PiModelInfo
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("api")] public string? Api { get; init; }
    [JsonPropertyName("provider")] public string? Provider { get; init; }
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; init; }
    [JsonPropertyName("reasoning")] public bool? Reasoning { get; init; }
    [JsonPropertyName("input")] public IReadOnlyList<string> Input { get; init; } = [];
    [JsonPropertyName("cost")] public PiCost? Cost { get; init; }
    [JsonPropertyName("contextWindow")] public int? ContextWindow { get; init; }
    [JsonPropertyName("maxTokens")] public int? MaxTokens { get; init; }
}

/// <summary>Model pricing metadata or usage cost details.</summary>
internal sealed record PiCost
{
    [JsonPropertyName("input")] public double Input { get; init; }
    [JsonPropertyName("output")] public double Output { get; init; }
    [JsonPropertyName("cacheRead")] public double CacheRead { get; init; }
    [JsonPropertyName("cacheWrite")] public double CacheWrite { get; init; }
    [JsonPropertyName("total")] public double? Total { get; init; }
}

/// <summary>Token usage reported on assistant messages.</summary>
internal sealed record PiUsage
{
    [JsonPropertyName("input")] public int Input { get; init; }
    [JsonPropertyName("output")] public int Output { get; init; }
    [JsonPropertyName("cacheRead")] public int CacheRead { get; init; }
    [JsonPropertyName("cacheWrite")] public int CacheWrite { get; init; }
    [JsonPropertyName("totalTokens")] public int TotalTokens { get; init; }
    [JsonPropertyName("cost")] public PiCost? Cost { get; init; }
}
