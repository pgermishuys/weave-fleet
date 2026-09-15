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

/// <summary>
/// Assistant turn — contains the model's response content blocks. Claude Code writes one line per
/// content block, so several lines can share a message id.
/// </summary>
internal sealed record ClaudeCodeAssistantMessage : ClaudeCodeStreamMessage
{
    [JsonPropertyName("message")] public ClaudeCodeApiMessage? Message { get; init; }

    /// <summary>The sub-agent call this message belongs to; null for the main conversation.</summary>
    [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; init; }
}

/// <summary>User turn — in a print-mode stream, this carries the results of the tools the assistant called.</summary>
internal sealed record ClaudeCodeUserMessage : ClaudeCodeStreamMessage
{
    [JsonPropertyName("message")] public ClaudeCodeApiMessage? Message { get; init; }

    /// <summary>The sub-agent call this message belongs to; null for the main conversation.</summary>
    [JsonPropertyName("parent_tool_use_id")] public string? ParentToolUseId { get; init; }
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
}
