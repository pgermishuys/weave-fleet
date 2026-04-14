using System.Text.Json;
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
        var partsJson = JsonSerializer.Serialize(message.Parts, SerializerOptions);
        return new PersistedMessage
        {
            Id = message.Id,
            SessionId = sessionId,
            Role = message.Role,
            PartsJson = partsJson,
            Timestamp = message.Timestamp.ToString("O"),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            AgentName = message.Agent,
        };
    }

    /// <summary>
    /// Converts a <see cref="PersistedMessage"/> back to a <see cref="HarnessMessage"/>,
    /// deserializing the polymorphic <see cref="MessagePart"/> list from JSON.
    /// </summary>
    public static HarnessMessage ToHarnessMessage(PersistedMessage persisted)
    {
        var parts = JsonSerializer.Deserialize<IReadOnlyList<MessagePart>>(
            persisted.PartsJson, SerializerOptions) ?? [];

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
        };
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
        };
    }
}
