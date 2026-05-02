using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Shared utility for stripping reasoning content from event payloads and message part lists.
/// Used by both client-delivery sanitization (Api layer) and durable-storage sanitization (Application layer).
/// </summary>
public static class ReasoningFilter
{
    private const string ReasoningType = "reasoning";

    /// <summary>
    /// Filters reasoning parts from a <c>message.created</c> or <c>message.updated</c> payload.
    /// Returns an empty <c>parts</c> array if the message contains only reasoning parts.
    /// Returns the original payload clone if no reasoning parts are present.
    /// </summary>
    public static JsonElement? FilterMessageEventPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return payload.Clone();

        if (!payload.TryGetProperty("parts", out var partsElement) || partsElement.ValueKind != JsonValueKind.Array)
            return payload.Clone();

        JsonArray? sanitizedParts = null;
        var removedAny = false;

        foreach (var part in partsElement.EnumerateArray())
        {
            if (IsReasoningPartElement(part))
            {
                removedAny = true;
                continue;
            }

            sanitizedParts ??= [];
            sanitizedParts.Add(JsonNode.Parse(part.GetRawText()));
        }

        if (!removedAny)
            return payload.Clone();

        var payloadNode = JsonNode.Parse(payload.GetRawText())?.AsObject();
        if (payloadNode is null)
            return payload.Clone();

        payloadNode["parts"] = sanitizedParts ?? [];
        using var doc = JsonDocument.Parse(payloadNode.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Returns <c>true</c> if a <c>message.part.updated</c> payload carries a reasoning part
    /// and should be suppressed from delivery or storage.
    /// </summary>
    public static bool IsReasoningPartEvent(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        if (!payload.TryGetProperty("part", out var partElement) || partElement.ValueKind != JsonValueKind.Object)
            return false;

        if (!partElement.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            return false;

        return string.Equals(typeElement.GetString(), ReasoningType, StringComparison.Ordinal);
    }

    /// <summary>
    /// Filters reasoning parts from a list of <see cref="MessagePart"/> instances for durable storage.
    /// </summary>
    public static MessagePart[] FilterDurableParts(IReadOnlyList<MessagePart> parts)
        => parts.Where(static part => part is not ReasoningPart).ToArray();

    private static bool IsReasoningPartElement(JsonElement part)
        => part.ValueKind == JsonValueKind.Object
            && part.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            && string.Equals(typeElement.GetString(), ReasoningType, StringComparison.Ordinal);
}
