using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

internal sealed record RawToolPart
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("messageId")] public string MessageId { get; init; } = string.Empty;
    [JsonPropertyName("callId")] public string? CallId { get; init; }
    [JsonPropertyName("tool")] public string? Tool { get; init; }
    [JsonPropertyName("state")] public JsonElement? State { get; init; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
}

/// <summary>
/// Deserializes OpenCode message parts while tolerating discriminator ordering differences.
/// </summary>
internal static class OpenCodePartDeserializer
{
    internal static OpenCodeMessagePart? DeserializePart(JsonElement partEl)
    {
        if (partEl.ValueKind != JsonValueKind.Object)
            return null;

        if (!partEl.TryGetProperty("type", out var typeEl))
            return null;

        return typeEl.GetString() switch
        {
            "text" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeTextPart),
            "tool" => DeserializeToolPart(partEl),
            "reasoning" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeReasoningPart),
            "step-start" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeStepStartPart),
            "step-finish" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeStepFinishPart),
            "file" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeFilePart),
            "agent" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeAgentPart),
            "subtask" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeSubtaskPart),
            "snapshot" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeSnapshotPart),
            "patch" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodePatchPart),
            "retry" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeRetryPart),
            "compaction" => partEl.Deserialize(OpenCodeJsonContext.Default.OpenCodeCompactionPart),
            _ => null,
        };
    }

    internal static OpenCodeToolPart? DeserializeToolPart(JsonElement partEl)
    {
        var rawPart = partEl.Deserialize(OpenCodeJsonContext.Default.RawToolPart);
        if (rawPart is null)
            return null;

        return new OpenCodeToolPart
        {
            Id = rawPart.Id,
            SessionId = rawPart.SessionId,
            MessageId = rawPart.MessageId,
            CallId = rawPart.CallId,
            Tool = rawPart.Tool,
            State = DeserializeToolState(rawPart.State),
            Metadata = rawPart.Metadata,
        };
    }

    private static OpenCodeToolState? DeserializeToolState(JsonElement? stateEl)
    {
        if (!stateEl.HasValue)
            return null;

        var state = stateEl.Value;
        if (state.ValueKind != JsonValueKind.Object)
            return null;

        if (!state.TryGetProperty("status", out var statusEl))
            return state.Deserialize(OpenCodeJsonContext.Default.OpenCodeToolState);

        return statusEl.GetString() switch
        {
            "pending" => state.Deserialize(OpenCodeJsonContext.Default.OpenCodeToolPending),
            "running" => state.Deserialize(OpenCodeJsonContext.Default.OpenCodeToolRunning),
            "completed" => state.Deserialize(OpenCodeJsonContext.Default.OpenCodeToolCompleted),
            "error" => state.Deserialize(OpenCodeJsonContext.Default.OpenCodeToolError),
            _ => state.Deserialize(OpenCodeJsonContext.Default.OpenCodeToolState),
        };
    }
}
