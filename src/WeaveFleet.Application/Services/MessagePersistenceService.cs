using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Converts between <see cref="HarnessMessage"/> and <see cref="PersistedMessage"/>,
/// encapsulating the JSON serialization/deserialization of the polymorphic <see cref="MessagePart"/> list.
/// </summary>
public sealed class MessagePersistenceService
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Converts a <see cref="HarnessMessage"/> to a <see cref="PersistedMessage"/>,
    /// serializing the polymorphic <see cref="MessagePart"/> list as JSON.
    /// </summary>
    public static PersistedMessage ToPersistedMessage(string sessionId, HarnessMessage message)
    {
        var partsJson = JsonSerializer.Serialize(FilterDurableParts(message.Parts), SerializerOptions);
        return new PersistedMessage
        {
            Id = message.Id,
            SessionId = sessionId,
            Role = message.Role,
            PartsJson = partsJson,
            Timestamp = message.Timestamp.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            AgentName = message.Agent,
            ModelId = message.ModelId,
        };
    }

    /// <summary>
    /// Creates a synthetic user message for durable prompt history.
    /// </summary>
    public static HarnessMessage CreateUserPromptMessage(string prompt, DateTimeOffset timestamp)
        => CreateUserPromptMessage(prompt, timestamp, agentName: null);

    /// <summary>
    /// Creates a synthetic user message for durable prompt history.
    /// </summary>
    public static HarnessMessage CreateUserPromptMessage(string prompt, DateTimeOffset timestamp, string? agentName)
    {
        return new HarnessMessage
        {
            Id = $"user-{Guid.NewGuid():N}",
            Role = "user",
            Parts = [new TextPart(prompt)],
            Timestamp = timestamp,
            Agent = agentName,
        };
    }

    /// <summary>
    /// Creates a synthetic user message representing a slash command for durable history.
    /// </summary>
    public static HarnessMessage CreateUserCommandMessage(CommandOptions options, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sanitizedArgs = options.Arguments?.ReplaceLineEndings(" ");
        var prompt = string.IsNullOrWhiteSpace(sanitizedArgs)
            ? $"/{options.Command}"
            : $"/{options.Command} {sanitizedArgs}";

        return CreateUserPromptMessage(prompt, timestamp, options.Agent);
    }

    /// <summary>
    /// Converts a <see cref="PersistedMessage"/> back to a <see cref="HarnessMessage"/>,
    /// deserializing the polymorphic <see cref="MessagePart"/> list from JSON.
    /// </summary>
    public static HarnessMessage ToHarnessMessage(PersistedMessage persisted)
    {
        var parts = FilterDurableParts(JsonSerializer.Deserialize<IReadOnlyList<MessagePart>>(
            persisted.PartsJson, SerializerOptions) ?? []);

        return new HarnessMessage
        {
            Id = persisted.Id,
            Role = persisted.Role,
            Parts = parts,
            Timestamp = DateTimeOffset.Parse(
                persisted.Timestamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
            Agent = persisted.AgentName,
            ModelId = persisted.ModelId,
        };
    }

    /// <summary>
    /// Batch conversion from <see cref="PersistedMessage"/> list to <see cref="HarnessMessage"/> list.
    /// </summary>
    public static IReadOnlyList<HarnessMessage> ToHarnessMessages(IReadOnlyList<PersistedMessage> persisted)
    {
        var result = new List<HarnessMessage>(persisted.Count);
        foreach (var msg in persisted)
            result.Add(ToHarnessMessage(msg));
        return result;
    }

    /// <summary>
    /// Serializes an event payload for durable outbox storage.
    /// </summary>
    public static string SerializePayload<TPayload>(TPayload payload)
        => JsonSerializer.Serialize(payload, SerializerOptions);

    /// <summary>
    /// Removes reasoning-only durable event payload content before outbox storage.
    /// </summary>
    public static JsonElement? SanitizeDurableEventPayload(string eventType, JsonElement? payload)
    {
        if (!payload.HasValue)
            return JsonSerializer.SerializeToElement(new { });

        if (eventType is "message.created" or "message.updated")
            return SanitizeMessageLifecyclePayload(payload.Value);

        if (eventType == "message.part.updated")
        {
            var payloadValue = payload.Value;
            if (payloadValue.ValueKind != JsonValueKind.Object)
                return payloadValue.Clone();

            if (!payloadValue.TryGetProperty("part", out var partElement)
                || partElement.ValueKind != JsonValueKind.Object
                || !partElement.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                return payloadValue.Clone();
            }

            return string.Equals(typeElement.GetString(), "reasoning", StringComparison.Ordinal)
                ? null
                : payloadValue.Clone();
        }

        return payload.Value.Clone();
    }

    /// <summary>
    /// Builds a committed message payload from the persisted snapshot so reconnect replay
    /// can consume the same final text content as durable history.
    /// </summary>
    public static JsonElement BuildCommittedMessagePayload(PersistedMessage persisted)
    {
        var message = ToHarnessMessage(persisted);
        var parts = new List<JsonElement>(message.Parts.Count);

        for (var index = 0; index < message.Parts.Count; index++)
        {
            var partPayload = BuildCommittedMessagePartPayload(message.Id, persisted.SessionId, message.Parts[index], index);
            if (partPayload.HasValue)
                parts.Add(partPayload.Value);
        }

        return JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = message.Id,
                role = message.Role,
                sessionID = persisted.SessionId,
                agent = message.Agent,
                modelID = message.ModelId,
                time = new { created = message.Timestamp.ToUnixTimeMilliseconds() },
            },
            parts,
        }, SerializerOptions);
    }

    /// <summary>
    /// Merges a new <see cref="MessagePart"/> into an existing <see cref="PersistedMessage"/>,
    /// returning a new <see cref="PersistedMessage"/> with the updated parts.
    /// </summary>
    /// <remarks>
    /// Part matching strategy:
    /// - <see cref="ToolUsePart"/>: matched by <c>ToolCallId</c>. Replaced in-place when found, appended otherwise.
    /// - <see cref="TextPart"/>: replaces the first existing <see cref="TextPart"/> if one exists, otherwise appended.
    /// - <see cref="ReasoningPart"/>: replaces the first existing <see cref="ReasoningPart"/> if one exists, otherwise appended.
    /// </remarks>
    public static PersistedMessage MergePart(PersistedMessage existing, MessagePart newPart)
        => MergePartAndMetadata(existing, newPart, role: null, agentName: null);

    /// <summary>
    /// Appends buffered live text to the first persisted <see cref="TextPart"/>, or creates one when absent.
    /// Used as a fallback when a harness streams ephemeral text deltas before an authoritative text snapshot arrives.
    /// </summary>
    public static PersistedMessage MergeTextDeltaAndMetadata(
        PersistedMessage existing,
        string deltaText,
        string? role,
        string? agentName)
    {
        if (string.IsNullOrEmpty(deltaText))
            return MergeMetadata(existing, role ?? existing.Role, agentName ?? existing.AgentName);

        var parts = JsonSerializer.Deserialize<List<MessagePart>>(existing.PartsJson, SerializerOptions) ?? [];
        var idx = parts.FindIndex(p => p is TextPart);
        if (idx >= 0)
        {
            var existingText = ((TextPart)parts[idx]).Text;
            parts[idx] = new TextPart(existingText + deltaText);
        }
        else
        {
            parts.Add(new TextPart(deltaText));
        }

        return new PersistedMessage
        {
            Id = existing.Id,
            SessionId = existing.SessionId,
            Role = role ?? existing.Role,
            PartsJson = JsonSerializer.Serialize(parts, SerializerOptions),
            Timestamp = existing.Timestamp,
            CreatedAt = existing.CreatedAt,
            AgentName = agentName ?? existing.AgentName,
            ModelId = existing.ModelId,
        };
    }

    /// <summary>
     /// Merges a new <see cref="MessagePart"/> into an existing <see cref="PersistedMessage"/>,
     /// backfilling message metadata when available.
     /// </summary>
    public static PersistedMessage MergePartAndMetadata(
        PersistedMessage existing,
        MessagePart newPart,
        string? role,
        string? agentName)
    {
        if (newPart is ReasoningPart)
            return MergeMetadata(existing, role ?? existing.Role, agentName ?? existing.AgentName);

        var parts = JsonSerializer.Deserialize<List<MessagePart>>(existing.PartsJson, SerializerOptions) ?? [];

        switch (newPart)
        {
            case ToolUsePart toolPart:
            {
                var idx = parts.FindIndex(p => p is ToolUsePart t && t.ToolCallId == toolPart.ToolCallId);
                if (idx >= 0)
                    parts[idx] = toolPart;
                else
                    parts.Add(toolPart);
                break;
            }
            case TextPart textPart:
            {
                var idx = parts.FindIndex(p => p is TextPart);
                if (idx >= 0)
                    parts[idx] = textPart;
                else
                    parts.Add(textPart);
                break;
            }
            case ReasoningPart reasoningPart:
            {
                var idx = parts.FindIndex(p => p is ReasoningPart);
                if (idx >= 0)
                    parts[idx] = reasoningPart;
                else
                    parts.Add(reasoningPart);
                break;
            }
            case FilePart filePart:
            {
                var idx = parts.FindIndex(p => p is FilePart f && f.PartId == filePart.PartId);
                if (idx >= 0)
                    parts[idx] = filePart;
                else
                    parts.Add(filePart);
                break;
            }
            case StepFinishPart stepFinishPart:
            {
                var idx = parts.FindIndex(p => p is StepFinishPart s && s.Index == stepFinishPart.Index);
                if (idx >= 0)
                    parts[idx] = stepFinishPart;
                else
                    parts.Add(stepFinishPart);
                break;
            }
            default:
                parts.Add(newPart);
                break;
        }

        return new PersistedMessage
        {
            Id = existing.Id,
            SessionId = existing.SessionId,
            Role = role ?? existing.Role,
            PartsJson = JsonSerializer.Serialize(parts, SerializerOptions),
            Timestamp = existing.Timestamp,
            CreatedAt = existing.CreatedAt,
            AgentName = agentName ?? existing.AgentName,
            ModelId = existing.ModelId,
        };
    }

    private static JsonElement? BuildCommittedMessagePartPayload(
        string messageId,
        string sessionId,
        MessagePart part,
        int index)
    {
        return part switch
        {
            TextPart textPart => JsonSerializer.SerializeToElement(new
            {
                id = $"{messageId}-text-{index}",
                messageID = messageId,
                sessionID = sessionId,
                type = "text",
                text = textPart.Text,
            }),
            _ => null,
        };
    }

    private static MessagePart[] FilterDurableParts(IReadOnlyList<MessagePart> parts)
        => parts.Where(static part => part is not ReasoningPart).ToArray();

    private static JsonElement? SanitizeMessageLifecyclePayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return payload.Clone();

        if (!payload.TryGetProperty("parts", out var partsElement) || partsElement.ValueKind != JsonValueKind.Array)
            return payload.Clone();

        var sanitizedParts = new List<JsonElement>();
        var removedAny = false;

        foreach (var part in partsElement.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && string.Equals(typeElement.GetString(), "reasoning", StringComparison.Ordinal))
            {
                removedAny = true;
                continue;
            }

            sanitizedParts.Add(part.Clone());
        }

        if (!removedAny)
            return payload.Clone();

        var payloadNode = JsonNode.Parse(payload.GetRawText())?.AsObject();
        if (payloadNode is null)
            return payload.Clone();

        payloadNode["parts"] = JsonSerializer.SerializeToNode(sanitizedParts);
        return JsonSerializer.SerializeToElement(payloadNode);
    }

    /// <summary>
    /// Merges authoritative message metadata into an existing persisted message without altering parts.
    /// </summary>
    public static PersistedMessage MergeMetadata(
        PersistedMessage existing,
        string role,
        string? agentName)
    {
        return new PersistedMessage
        {
            Id = existing.Id,
            SessionId = existing.SessionId,
            Role = role,
            PartsJson = existing.PartsJson,
            Timestamp = existing.Timestamp,
            CreatedAt = existing.CreatedAt,
            AgentName = agentName ?? existing.AgentName,
            ModelId = existing.ModelId,
        };
    }
}
