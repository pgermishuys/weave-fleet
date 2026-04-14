using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

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
            "text" => partEl.Deserialize<OpenCodeTextPart>(OpenCodeJsonOptions.Default),
            "tool" => DeserializeToolPart(partEl),
            "reasoning" => partEl.Deserialize<OpenCodeReasoningPart>(OpenCodeJsonOptions.Default),
            "step-start" => partEl.Deserialize<OpenCodeStepStartPart>(OpenCodeJsonOptions.Default),
            "step-finish" => partEl.Deserialize<OpenCodeStepFinishPart>(OpenCodeJsonOptions.Default),
            "file" => partEl.Deserialize<OpenCodeFilePart>(OpenCodeJsonOptions.Default),
            "agent" => partEl.Deserialize<OpenCodeAgentPart>(OpenCodeJsonOptions.Default),
            "subtask" => partEl.Deserialize<OpenCodeSubtaskPart>(OpenCodeJsonOptions.Default),
            "snapshot" => partEl.Deserialize<OpenCodeSnapshotPart>(OpenCodeJsonOptions.Default),
            "patch" => partEl.Deserialize<OpenCodePatchPart>(OpenCodeJsonOptions.Default),
            "retry" => partEl.Deserialize<OpenCodeRetryPart>(OpenCodeJsonOptions.Default),
            "compaction" => partEl.Deserialize<OpenCodeCompactionPart>(OpenCodeJsonOptions.Default),
            _ => null,
        };
    }

    internal static OpenCodeToolPart? DeserializeToolPart(JsonElement partEl)
    {
        var rawPart = partEl.Deserialize<RawToolPart>(OpenCodeJsonOptions.Default);
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
            return state.Deserialize<OpenCodeToolState>(OpenCodeJsonOptions.Default);

        return statusEl.GetString() switch
        {
            "pending" => state.Deserialize<OpenCodeToolPending>(OpenCodeJsonOptions.Default),
            "running" => state.Deserialize<OpenCodeToolRunning>(OpenCodeJsonOptions.Default),
            "completed" => state.Deserialize<OpenCodeToolCompleted>(OpenCodeJsonOptions.Default),
            "error" => state.Deserialize<OpenCodeToolError>(OpenCodeJsonOptions.Default),
            _ => state.Deserialize<OpenCodeToolState>(OpenCodeJsonOptions.Default),
        };
    }

    private sealed record RawToolPart
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
        [JsonPropertyName("messageId")] public string MessageId { get; init; } = string.Empty;
        [JsonPropertyName("callId")] public string? CallId { get; init; }
        [JsonPropertyName("tool")] public string? Tool { get; init; }
        [JsonPropertyName("state")] public JsonElement? State { get; init; }
        [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
    }
}
