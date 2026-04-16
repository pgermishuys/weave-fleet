using System.Text.Json;
using WeaveFleet.Application.Services;
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

        if (!EventTypeMetadata.Classify(eventType).RequiresReasoningFilter)
            return payload.Value.Clone();

        if (eventType is EventTypes.MessageCreated or EventTypes.MessageUpdated)
            return ReasoningFilter.FilterMessageEventPayload(payload.Value);

        // message.part.updated — suppress reasoning parts entirely
        var payloadValue = payload.Value;
        if (payloadValue.ValueKind != JsonValueKind.Object)
            return payloadValue.Clone();

        return ReasoningFilter.IsReasoningPartEvent(payloadValue)
            ? null
            : payloadValue.Clone();
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
