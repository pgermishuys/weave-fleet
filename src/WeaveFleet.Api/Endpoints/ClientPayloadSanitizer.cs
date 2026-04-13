using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Api.Endpoints;

internal static class ClientPayloadSanitizer
{
    public static IReadOnlyList<HarnessMessage> SanitizeMessages(IReadOnlyList<HarnessMessage> messages)
    {
        if (messages.Count == 0)
            return messages;

        List<HarnessMessage>? sanitized = null;

        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var sanitizedMessage = SanitizeMessage(message);

            if (sanitizedMessage is null)
            {
                sanitized ??= new List<HarnessMessage>(messages.Count);
                for (int j = 0; j < i; j++)
                    sanitized.Add(messages[j]);
                continue;
            }

            if (sanitized is null)
            {
                if (!ReferenceEquals(sanitizedMessage, message))
                {
                    sanitized = new List<HarnessMessage>(messages.Count);
                    for (int j = 0; j < i; j++)
                        sanitized.Add(messages[j]);
                    sanitized.Add(sanitizedMessage);
                }

                continue;
            }

            sanitized.Add(sanitizedMessage);
        }

        return sanitized ?? messages;
    }

    public static JsonElement? SanitizeEventPayload(string eventType, JsonElement? payload)
    {
        if (!payload.HasValue)
            return JsonSerializer.SerializeToElement(new { });

        if (string.Equals(eventType, "message.created", StringComparison.Ordinal)
            || string.Equals(eventType, "message.updated", StringComparison.Ordinal))
        {
            return SanitizeMessageEventPayload(payload.Value);
        }

        if (!string.Equals(eventType, "message.part.updated", StringComparison.Ordinal))
            return payload.Value.Clone();

        var payloadValue = payload.Value;
        if (payloadValue.ValueKind != JsonValueKind.Object)
            return payloadValue.Clone();

        if (!payloadValue.TryGetProperty("part", out var partElement) || partElement.ValueKind != JsonValueKind.Object)
            return payloadValue.Clone();

        if (!partElement.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            return payloadValue.Clone();

        return string.Equals(typeElement.GetString(), "reasoning", StringComparison.Ordinal)
            ? null
            : payloadValue.Clone();
    }

    private static JsonElement? SanitizeMessageEventPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return payload.Clone();

        if (!payload.TryGetProperty("parts", out var partsElement) || partsElement.ValueKind != JsonValueKind.Array)
            return payload.Clone();

        JsonArray? sanitizedParts = null;
        var removedAny = false;

        foreach (var part in partsElement.EnumerateArray())
        {
            if (IsReasoningPart(part))
            {
                removedAny = true;
                continue;
            }

            sanitizedParts ??= [];
            sanitizedParts.Add(JsonNode.Parse(part.GetRawText()));
        }

        if (!removedAny)
            return payload.Clone();

        var role = payload.TryGetProperty("info", out var infoElement)
            && infoElement.ValueKind == JsonValueKind.Object
            && infoElement.TryGetProperty("role", out var roleElement)
            && roleElement.ValueKind == JsonValueKind.String
                ? roleElement.GetString()
                : null;

        if (sanitizedParts is null && string.Equals(role, "assistant", StringComparison.Ordinal))
            return null;

        var payloadNode = JsonNode.Parse(payload.GetRawText())?.AsObject();
        if (payloadNode is null)
            return payload.Clone();

        payloadNode["parts"] = sanitizedParts ?? [];
        return JsonSerializer.SerializeToElement(payloadNode);
    }

    private static bool IsReasoningPart(JsonElement part)
    {
        return part.ValueKind == JsonValueKind.Object
            && part.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            && string.Equals(typeElement.GetString(), "reasoning", StringComparison.Ordinal);
    }

    private static HarnessMessage? SanitizeMessage(HarnessMessage message)
    {
        if (message.Parts.Count == 0)
            return message;

        List<MessagePart>? sanitizedParts = null;

        for (int i = 0; i < message.Parts.Count; i++)
        {
            var part = message.Parts[i];
            if (part is ReasoningPart)
            {
                sanitizedParts ??= new List<MessagePart>(message.Parts.Count);
                for (int j = 0; j < i; j++)
                    sanitizedParts.Add(message.Parts[j]);
                continue;
            }

            sanitizedParts?.Add(part);
        }

        if (sanitizedParts is null)
            return message;

        return sanitizedParts.Count == 0
            ? null
            : message with { Parts = sanitizedParts };
    }
}
