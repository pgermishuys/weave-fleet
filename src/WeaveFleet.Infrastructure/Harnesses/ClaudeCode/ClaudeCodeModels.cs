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
[JsonDerivedType(typeof(ClaudeCodeResultMessage), "result")]
internal record ClaudeCodeStreamMessage;

/// <summary>System message (e.g. init). Contains session metadata.</summary>
internal sealed record ClaudeCodeSystemMessage : ClaudeCodeStreamMessage
{
    [JsonPropertyName("subtype")] public string? Subtype { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("tools")] public JsonElement? Tools { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("mcp_servers")] public JsonElement? McpServers { get; init; }
}

/// <summary>Assistant turn — contains the model's response content blocks.</summary>
internal sealed record ClaudeCodeAssistantMessage : ClaudeCodeStreamMessage
{
    [JsonPropertyName("message")] public ClaudeCodeApiMessage? Message { get; init; }
}

/// <summary>Final result line — contains cost, usage, and outcome.</summary>
internal sealed record ClaudeCodeResultMessage : ClaudeCodeStreamMessage
{
    [JsonPropertyName("subtype")] public string? Subtype { get; init; }
    [JsonPropertyName("result")] public string? Result { get; init; }
    [JsonPropertyName("num_turns")] public int? NumTurns { get; init; }
    [JsonPropertyName("duration_ms")] public long? DurationMs { get; init; }
    [JsonPropertyName("usage")] public ClaudeCodeUsage? Usage { get; init; }
    [JsonPropertyName("total_cost_usd")] public decimal? TotalCostUsd { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
}

// ---------------------------------------------------------------------------
// API message + content blocks
// ---------------------------------------------------------------------------

/// <summary>The Anthropic API-shaped message inside an assistant turn.</summary>
internal sealed record ClaudeCodeApiMessage
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("content")] public IReadOnlyList<ClaudeCodeContentBlock>? Content { get; init; }
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
[JsonDerivedType(typeof(ClaudeCodeToolUseBlock), "tool_use")]
[JsonDerivedType(typeof(ClaudeCodeToolResultBlock), "tool_result")]
internal record ClaudeCodeContentBlock;

/// <summary>Plain text content block.</summary>
internal sealed record ClaudeCodeTextBlock : ClaudeCodeContentBlock
{
    [JsonPropertyName("text")] public string? Text { get; init; }
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
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("is_error")] public bool? IsError { get; init; }
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
}
