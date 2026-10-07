using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>Shared <see cref="JsonSerializerOptions"/> for all Claude Code NDJSON serialization.</summary>
internal static class ClaudeCodeJsonOptions
{
    internal static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowOutOfOrderMetadataProperties = true,
    };
}

// ---------------------------------------------------------------------------
// Top-level stream messages — discriminated by "type"
// ---------------------------------------------------------------------------

/// <summary>Base type for all Claude Code NDJSON stdout lines.</summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(ClaudeCodeSystemMessage), "system")]
[JsonDerivedType(typeof(ClaudeCodeAssistantMessage), "assistant")]
[JsonDerivedType(typeof(ClaudeCodeUserMessage), "user")]
[JsonDerivedType(typeof(ClaudeCodeResultMessage), "result")]
[JsonDerivedType(typeof(ClaudeCodeControlRequest), "control_request")]
[JsonDerivedType(typeof(ClaudeCodeControlCancelRequest), "control_cancel_request")]
[JsonDerivedType(typeof(ClaudeCodeControlResponse), "control_response")]
[JsonDerivedType(typeof(ClaudeCodeStreamEvent), "stream_event")]
[JsonDerivedType(typeof(ClaudeCodeRateLimitEvent), "rate_limit_event")]
internal record ClaudeCodeStreamMessage;

/// <summary>
/// How much of the claude.ai account's usage limits are used, when they change: only with a claude.ai login (never
/// with an API key or a gateway). Recorded from claude 2.1.290:
/// <c>{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1791737124,"rateLimitType":"seven_day","utilization":0.63,"isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.82,"resetsAt":1791344624},…}}}</c>.
/// </summary>
internal sealed record ClaudeCodeRateLimitEvent : ClaudeCodeStreamMessage
{
    [JsonPropertyName("rate_limit_info")] public ClaudeCodeRateLimitInfo? RateLimitInfo { get; init; }
}

/// <summary>What a <see cref="ClaudeCodeRateLimitEvent"/> says.</summary>
internal sealed record ClaudeCodeRateLimitInfo
{
    /// <summary><c>allowed</c>, <c>allowed_warning</c> or <c>rejected</c>, for <see cref="RateLimitType"/>.</summary>
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>When <see cref="RateLimitType"/> resets, in seconds since the epoch.</summary>
    [JsonPropertyName("resetsAt")] public long? ResetsAt { get; init; }

    /// <summary>The window the status is about: <c>five_hour</c>, <c>seven_day</c>, <c>seven_day_opus</c>, <c>seven_day_sonnet</c>, …</summary>
    [JsonPropertyName("rateLimitType")] public string? RateLimitType { get; init; }

    /// <summary>How much of <see cref="RateLimitType"/> is used, from 0 to 1.</summary>
    [JsonPropertyName("utilization")] public double? Utilization { get; init; }

    [JsonPropertyName("isUsingOverage")] public bool? IsUsingOverage { get; init; }
    [JsonPropertyName("overageStatus")] public string? OverageStatus { get; init; }

    /// <summary>Every window Claude Code knows, by name: each one's use and reset.</summary>
    [JsonPropertyName("unifiedWindows")] public IReadOnlyDictionary<string, ClaudeCodeRateLimitWindow>? UnifiedWindows { get; init; }
}

/// <summary>One window in <see cref="ClaudeCodeRateLimitInfo.UnifiedWindows"/>.</summary>
internal sealed record ClaudeCodeRateLimitWindow
{
    [JsonPropertyName("utilization")] public double? Utilization { get; init; }
    [JsonPropertyName("resetsAt")] public long? ResetsAt { get; init; }
}

/// <summary>
/// A piece of the message the model is writing, with <c>--include-partial-messages</c>: one of the Messages API's
/// streaming events (<c>message_start</c>, <c>content_block_start</c>, <c>content_block_delta</c>, …). The finished
/// block still follows as an <c>assistant</c> line, just before its <c>content_block_stop</c>.
/// </summary>
internal sealed record ClaudeCodeStreamEvent : ClaudeCodeStreamMessage
{
    [JsonPropertyName("event")] public ClaudeCodeStreamEventBody? Event { get; init; }

    /// <summary>The sub-agent call this message belongs to; null for the main conversation.</summary>
    [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; init; }
}

/// <summary>What a <see cref="ClaudeCodeStreamEvent"/> says.</summary>
internal sealed record ClaudeCodeStreamEventBody
{
    [JsonPropertyName("type")] public string? Type { get; init; }

    /// <summary>On <c>message_start</c>: the message, whose id the finished blocks carry too.</summary>
    [JsonPropertyName("message")] public ClaudeCodeApiMessage? Message { get; init; }

    /// <summary>The content block a <c>content_block_*</c> event is about.</summary>
    [JsonPropertyName("index")] public int? Index { get; init; }

    /// <summary>On <c>content_block_start</c>: the block, still empty.</summary>
    [JsonPropertyName("content_block")] public ClaudeCodeContentBlock? ContentBlock { get; init; }

    /// <summary>On <c>content_block_delta</c>: what was added, e.g. <c>{ type: text_delta, text }</c>.</summary>
    [JsonPropertyName("delta")] public ClaudeCodeStreamDelta? Delta { get; init; }

    /// <summary>On <c>message_delta</c>: the message's usage, with its real output count.</summary>
    [JsonPropertyName("usage")] public ClaudeCodeUsage? Usage { get; init; }
}

/// <summary>A <c>content_block_delta</c>'s addition: <c>text_delta</c>, <c>thinking_delta</c>, or another kind Fleet doesn't show.</summary>
internal sealed record ClaudeCodeStreamDelta
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("thinking")] public string? Thinking { get; init; }

    /// <summary>On <c>message_delta</c>: why the model stopped (<c>end_turn</c>, <c>tool_use</c>, …).</summary>
    [JsonPropertyName("stop_reason")] public string? StopReason { get; init; }
}

/// <summary>
/// Claude Code asks its host something, with <c>--permission-prompt-tool stdio</c>: <c>can_use_tool</c> asks whether a
/// tool call may run. The host answers with a <c>control_response</c> line on stdin naming <see cref="RequestId"/>.
/// </summary>
internal sealed record ClaudeCodeControlRequest : ClaudeCodeStreamMessage
{
    [JsonPropertyName("request_id")] public string? RequestId { get; init; }
    [JsonPropertyName("request")] public ClaudeCodeControlRequestBody? Request { get; init; }
}

/// <summary>What a <see cref="ClaudeCodeControlRequest"/> asks.</summary>
internal sealed record ClaudeCodeControlRequestBody
{
    [JsonPropertyName("subtype")] public string? Subtype { get; init; }
    [JsonPropertyName("tool_name")] public string? ToolName { get; init; }
    [JsonPropertyName("input")] public JsonElement Input { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }

    /// <summary>The rules Claude Code offers for not asking again, e.g. <c>{ type: addRules, rules: [{ toolName, ruleContent }] }</c>.</summary>
    [JsonPropertyName("permission_suggestions")] public JsonElement? PermissionSuggestions { get; init; }

    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; init; }
}

/// <summary>Claude Code no longer needs the answer to request <see cref="RequestId"/> (the turn was stopped).</summary>
internal sealed record ClaudeCodeControlCancelRequest : ClaudeCodeStreamMessage
{
    [JsonPropertyName("request_id")] public string? RequestId { get; init; }
}

/// <summary>Claude Code's answer to a <c>control_request</c> Fleet sent it (an interrupt, a model change).</summary>
internal sealed record ClaudeCodeControlResponse : ClaudeCodeStreamMessage
{
    [JsonPropertyName("response")] public ClaudeCodeControlResponseBody? Response { get; init; }
}

/// <summary>What a <see cref="ClaudeCodeControlResponse"/> says: <c>success</c>, or <c>error</c> and why.</summary>
internal sealed record ClaudeCodeControlResponseBody
{
    [JsonPropertyName("subtype")] public string? Subtype { get; init; }
    [JsonPropertyName("request_id")] public string? RequestId { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>What a request that asks for something got, e.g. <c>get_task_output</c>'s <c>{ output, total_bytes, truncated }</c>.</summary>
    [JsonPropertyName("response")] public JsonElement? Response { get; init; }
}

/// <summary>
/// System message. <c>init</c> opens every turn, including one Claude Code starts by itself, and carries the session
/// metadata. <c>task_started</c>, <c>task_progress</c>, <c>task_updated</c>, <c>task_notification</c> and
/// <c>background_tasks_changed</c> report the work an agent leaves running: background shells, monitors and subagents.
/// </summary>
internal sealed record ClaudeCodeSystemMessage : ClaudeCodeStreamMessage
{
    [JsonPropertyName("subtype")] public string? Subtype { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("tools")] public JsonElement? Tools { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("mcp_servers")] public JsonElement? McpServers { get; init; }

    /// <summary>The task a <c>task_*</c> message is about.</summary>
    [JsonPropertyName("task_id")] public string? TaskId { get; init; }

    /// <summary>
    /// What runs the task: <c>local_bash</c> (a <c>Bash</c> command or a <c>Monitor</c>), <c>local_agent</c> (a subagent),
    /// or another kind.
    /// </summary>
    [JsonPropertyName("task_type")] public string? TaskType { get; init; }

    /// <summary>The tool call that started the task.</summary>
    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; init; }

    /// <summary>On <c>task_started</c>: false for a task its tool call waits on, which ends with the turn.</summary>
    [JsonPropertyName("is_backgrounded")] public bool? IsBackgrounded { get; init; }

    /// <summary>What the task is (<c>task_started</c>), or what it's doing now (<c>task_progress</c>).</summary>
    [JsonPropertyName("description")] public string? Description { get; init; }

    /// <summary>On a subagent's <c>task_started</c>: its agent, e.g. <c>general-purpose</c>.</summary>
    [JsonPropertyName("subagent_type")] public string? SubagentType { get; init; }

    /// <summary>
    /// On a subagent's <c>task_started</c>: what it was asked. A background subagent's conversation doesn't start with it
    /// as a user line, as a foreground one's does.
    /// </summary>
    [JsonPropertyName("prompt")] public string? Prompt { get; init; }

    /// <summary>
    /// On <c>task_notification</c>: <c>completed</c>, <c>failed</c> or <c>stopped</c>. On a <c>status</c> line:
    /// <c>compacting</c> while a compaction runs.
    /// </summary>
    [JsonPropertyName("status")] public string? Status { get; init; }

    /// <summary>On the <c>status</c> line that ends a compaction: <c>success</c> or <c>failed</c>.</summary>
    [JsonPropertyName("compact_result")] public string? CompactResult { get; init; }

    /// <summary>Why a compaction failed, with <see cref="CompactResult"/> <c>failed</c>.</summary>
    [JsonPropertyName("compact_error")] public string? CompactError { get; init; }

    /// <summary>On <c>compact_boundary</c>: <c>{ trigger: "manual" | "auto", pre_tokens, ... }</c>.</summary>
    [JsonPropertyName("compact_metadata")] public JsonElement? CompactMetadata { get; init; }

    /// <summary>On <c>task_notification</c>: the file the task's output went to.</summary>
    [JsonPropertyName("output_file")] public string? OutputFile { get; init; }

    /// <summary>On <c>task_notification</c>: how it ended, e.g. <c>… completed (exit code 0)</c>, or a subagent's reply.</summary>
    [JsonPropertyName("summary")] public string? Summary { get; init; }

    /// <summary>On <c>task_updated</c>: what changed, e.g. <c>{ status: killed }</c>.</summary>
    [JsonPropertyName("patch")] public ClaudeCodeTaskPatch? Patch { get; init; }

    /// <summary>On <c>background_tasks_changed</c>: every task still running.</summary>
    [JsonPropertyName("tasks")] public IReadOnlyList<ClaudeCodeBackgroundTask>? Tasks { get; init; }

    /// <summary>The sub-agent call a line is about, when it's a subagent's; null for the main conversation.</summary>
    [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; init; }

    /// <summary>On <c>api_retry</c>: which retry this is (from 1), out of <see cref="MaxRetries"/>.</summary>
    [JsonPropertyName("attempt")] public int? Attempt { get; init; }
    [JsonPropertyName("max_retries")] public int? MaxRetries { get; init; }

    /// <summary>On <c>api_retry</c>: how long Claude Code waits before it tries again.</summary>
    [JsonPropertyName("retry_delay_ms")] public long? RetryDelayMs { get; init; }

    /// <summary>On <c>api_retry</c>: the HTTP status the model call failed with (529, 429, 500…); null for a connection error.</summary>
    [JsonPropertyName("error_status")] public int? ErrorStatus { get; init; }

    /// <summary>On <c>api_retry</c>: what went wrong, e.g. <c>overloaded</c>, <c>rate_limit</c>, <c>server_error</c>.</summary>
    [JsonPropertyName("error")] public JsonElement? Error { get; init; }
}

/// <summary>What a <c>task_updated</c> message changed.</summary>
internal sealed record ClaudeCodeTaskPatch
{
    [JsonPropertyName("status")] public string? Status { get; init; }
}

/// <summary>A task in a <c>background_tasks_changed</c> list.</summary>
internal sealed record ClaudeCodeBackgroundTask
{
    [JsonPropertyName("task_id")] public string? TaskId { get; init; }
    [JsonPropertyName("task_type")] public string? TaskType { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

/// <summary>
/// Assistant turn — contains the model's response content blocks. Claude Code writes one line per
/// content block, so several lines can share a message id.
/// </summary>
internal sealed record ClaudeCodeAssistantMessage : ClaudeCodeStreamMessage
{
    [JsonPropertyName("message")] public ClaudeCodeApiMessage? Message { get; init; }

    /// <summary>The sub-agent call this message belongs to; null for the main conversation.</summary>
    [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; init; }

    /// <summary>
    /// On a message Claude Code wrote itself because the model call failed (<see cref="IsApiErrorMessage"/>): why, e.g.
    /// <c>rate_limit</c>, <c>overloaded</c>, <c>server_error</c>, <c>authentication_failed</c>, <c>billing_error</c>.
    /// </summary>
    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>The message is Claude Code's account of a failed model call ("You've hit your session limit · resets 3:43am"), not the model's.</summary>
    [JsonPropertyName("is_api_error_message")] public bool? IsApiErrorMessage { get; init; }
}

/// <summary>User turn — in a print-mode stream, this carries the results of the tools the assistant called.</summary>
internal sealed record ClaudeCodeUserMessage : ClaudeCodeStreamMessage
{
    [JsonPropertyName("message")] public ClaudeCodeApiMessage? Message { get; init; }

    /// <summary>
    /// What the tool reported besides its result, for the line's one tool result: an edit's <c>structuredPatch</c>, a
    /// created file's <c>type: create</c> and content.
    /// </summary>
    [JsonPropertyName("tool_use_result")] public JsonElement? ToolUseResult { get; init; }

    /// <summary>The sub-agent call this message belongs to; null for the main conversation.</summary>
    [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; init; }

    /// <summary>
    /// A line Claude Code wrote itself rather than the user: after a <c>compact_boundary</c>, the summary the model goes
    /// on from ("This session is being continued from a previous conversation…").
    /// </summary>
    [JsonPropertyName("isSynthetic")] public bool? IsSynthetic { get; init; }
}

/// <summary>Final result line — contains cost, usage, and outcome.</summary>
internal sealed record ClaudeCodeResultMessage : ClaudeCodeStreamMessage
{
    [JsonPropertyName("subtype")] public string? Subtype { get; init; }
    [JsonPropertyName("is_error")] public bool? IsError { get; init; }

    /// <summary>Why the run failed, e.g. "Reached maximum number of turns (1)".</summary>
    [JsonPropertyName("errors")] public IReadOnlyList<string>? Errors { get; init; }

    [JsonPropertyName("result")] public string? Result { get; init; }
    [JsonPropertyName("num_turns")] public int? NumTurns { get; init; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; init; }
    [JsonPropertyName("usage")] public ClaudeCodeUsage? Usage { get; init; }
    [JsonPropertyName("total_cost_usd")] public decimal? TotalCostUsd { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }

    /// <summary>
    /// Usage by model over the turn, keyed by model id: each with <c>contextWindow</c> and <c>maxOutputTokens</c>,
    /// the model's limits.
    /// </summary>
    [JsonPropertyName("modelUsage")] public JsonElement? ModelUsage { get; init; }

    /// <summary>The HTTP status of the model call that ended the turn, when one failed (429, 529); the result's <c>subtype</c> can still say <c>success</c>.</summary>
    [JsonPropertyName("api_error_status")] public int? ApiErrorStatus { get; init; }

    /// <summary>Why the turn ended, e.g. <c>api_error</c>, <c>blocking_limit</c>.</summary>
    [JsonPropertyName("terminal_reason")] public string? TerminalReason { get; init; }
}

// ---------------------------------------------------------------------------
// API message + content blocks
// ---------------------------------------------------------------------------

/// <summary>The Anthropic API-shaped message inside an assistant turn.</summary>
internal sealed record ClaudeCodeApiMessage
{
    [JsonPropertyName("id")] public string? Id { get; init; }

    [JsonPropertyName("content")]
    [JsonConverter(typeof(ClaudeCodeContentConverter))]
    public IReadOnlyList<ClaudeCodeContentBlock>? Content { get; init; }
    [JsonPropertyName("stop_reason")] public string? StopReason { get; init; }
    [JsonPropertyName("usage")] public ClaudeCodeUsage? Usage { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
}

/// <summary>Base type for Claude Code content blocks — discriminated by "type".</summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    IgnoreUnrecognizedTypeDiscriminators = true,
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(ClaudeCodeTextBlock), "text")]
[JsonDerivedType(typeof(ClaudeCodeThinkingBlock), "thinking")]
[JsonDerivedType(typeof(ClaudeCodeToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(ClaudeCodeToolResultBlock), "tool_result")]
internal record ClaudeCodeContentBlock;

/// <summary>Plain text content block.</summary>
internal sealed record ClaudeCodeTextBlock : ClaudeCodeContentBlock
{
    [JsonPropertyName("text")] public string? Text { get; init; }
}

/// <summary>Extended thinking block. The text is often empty: Claude Code only keeps the signature.</summary>
internal sealed record ClaudeCodeThinkingBlock : ClaudeCodeContentBlock
{
    [JsonPropertyName("thinking")] public string? Thinking { get; init; }
}

/// <summary>Tool invocation block (model requesting a tool call).</summary>
internal sealed record ClaudeCodeToolUseBlock : ClaudeCodeContentBlock
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("input")] public JsonElement Input { get; init; }
}

/// <summary>Tool result block (result of a tool invocation).</summary>
internal sealed record ClaudeCodeToolResultBlock : ClaudeCodeContentBlock
{
    [JsonPropertyName("tool_use_id")] public string? ToolUseId { get; init; }

    [JsonPropertyName("content")]
    [JsonConverter(typeof(ClaudeCodeToolResultContentConverter))]
    public string? Content { get; init; }

    [JsonPropertyName("is_error")] public bool? IsError { get; init; }
}

/// <summary>
/// Reads a message's <c>content</c>: a list of content blocks, or a string, which is one text block. A subagent's prompt
/// can come as either.
/// </summary>
internal sealed class ClaudeCodeContentConverter : JsonConverter<IReadOnlyList<ClaudeCodeContentBlock>?>
{
    public override IReadOnlyList<ClaudeCodeContentBlock>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return [new ClaudeCodeTextBlock { Text = reader.GetString() }];
        }

        var blocks = new List<ClaudeCodeContentBlock>();
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("A message's content is a string or a list of blocks.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var block = JsonSerializer.Deserialize(ref reader, ClaudeCodeJsonContext.Default.ClaudeCodeContentBlock);
            if (block is not null)
                blocks.Add(block);
        }

        return blocks;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ClaudeCodeContentBlock>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var block in value)
            JsonSerializer.Serialize(writer, block, ClaudeCodeJsonContext.Default.ClaudeCodeContentBlock);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Reads a tool result's <c>content</c>, which is either a string or a list of content blocks
/// (MCP tools, images), as text.
/// </summary>
internal sealed class ClaudeCodeToolResultContentConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            return root.GetRawText();

        var texts = new List<string>();
        foreach (var block in root.EnumerateArray())
        {
            var type = block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            if (type == "text" && block.TryGetProperty("text", out var text))
                texts.Add(text.GetString() ?? string.Empty);
            else if (type == "image")
                texts.Add("[image]");
            else
                texts.Add(block.GetRawText());
        }

        return string.Join("\n", texts);
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}

// ---------------------------------------------------------------------------
// Usage / cost
// ---------------------------------------------------------------------------

/// <summary>Token usage statistics.</summary>
internal sealed record ClaudeCodeUsage
{
    [JsonPropertyName("input_tokens")] public int InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; init; }
    [JsonPropertyName("cache_read_input_tokens")] public int? CacheReadInputTokens { get; init; }
    [JsonPropertyName("cache_creation_input_tokens")] public int? CacheCreationInputTokens { get; init; }

    /// <summary>How much of <see cref="OutputTokens"/> was thinking.</summary>
    [JsonPropertyName("output_tokens_details")] public ClaudeCodeOutputDetails? OutputTokensDetails { get; init; }

    /// <summary>
    /// On a result line, whose own counts add up the turn's calls: the calls one by one, of <c>type</c>
    /// <c>message</c> (or <c>compaction</c>, a summarising call).
    /// </summary>
    [JsonPropertyName("iterations")] public IReadOnlyList<ClaudeCodeUsageIteration>? Iterations { get; init; }
}

/// <summary>What a call's output was made of.</summary>
internal sealed record ClaudeCodeOutputDetails
{
    [JsonPropertyName("thinking_tokens")] public int? ThinkingTokens { get; init; }
}

/// <summary>One model call's usage on a result line (<see cref="ClaudeCodeUsage.Iterations"/>).</summary>
internal sealed record ClaudeCodeUsageIteration
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("input_tokens")] public int InputTokens { get; init; }
    [JsonPropertyName("output_tokens")] public int OutputTokens { get; init; }
    [JsonPropertyName("cache_read_input_tokens")] public int? CacheReadInputTokens { get; init; }
    [JsonPropertyName("cache_creation_input_tokens")] public int? CacheCreationInputTokens { get; init; }
}
