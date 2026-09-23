using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Builds Fleet's record of what a tool returned, a <c>message.part.updated</c> the harnesses add beside the tool part
/// so the session's stored history keeps its tool output. The record is stored but not sent to clients, who get the
/// result on the tool part.
/// </summary>
internal static class ToolResultEventBuilder
{
    /// <summary>
    /// Builds a tool-result event payload as a <see cref="JsonElement"/> wrapped in a <c>"part"</c> property.
    /// </summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="callId">The tool call ID.</param>
    /// <param name="content">The tool result content.</param>
    /// <param name="isError">Whether the result represents an error.</param>
    /// <returns>A <see cref="JsonElement"/> containing the serialized wrapped payload.</returns>
    internal static JsonElement BuildPayload(
        string messageId,
        string sessionId,
        string callId,
        string? content,
        bool isError)
    {
        var wrapper = new ToolResultPayloadWrapper
        {
            Part = new ToolResultPartContent
            {
                Type = "tool-result",
                MessageId = messageId,
                SessionId = sessionId,
                CallId = callId,
                Content = content,
                IsError = isError,
            }
        };

        return JsonSerializer.SerializeToElement(
            wrapper,
            ToolResultEventBuilderJsonContext.Default.ToolResultPayloadWrapper);
    }

    internal sealed record ToolResultPayloadWrapper
    {
        [JsonPropertyName("part")] public required ToolResultPartContent Part { get; init; }
    }

    internal sealed record ToolResultPartContent
    {
        [JsonPropertyName("type")] public required string Type { get; init; }
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("messageId")] public required string MessageId { get; init; }
        [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
        [JsonPropertyName("callId")] public required string CallId { get; init; }
        [JsonPropertyName("content")] public string? Content { get; init; }
        [JsonPropertyName("isError")] public bool IsError { get; init; }
    }
}

/// <summary>CamelCase + WhenWritingNull options for tool-result event payloads.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolResultEventBuilder.ToolResultPayloadWrapper))]
internal sealed partial class ToolResultEventBuilderJsonContext : JsonSerializerContext
{
}
