using System.Text.Json;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>
/// Maps OpenCode API DTOs to Weave Fleet domain types.
/// </summary>
internal static class OpenCodeMapper
{
    private static readonly string[] InfoPath = ["info"];
    private static readonly string[] PartPath = ["part"];
    private static readonly string[] ChildPath = ["child"];
    private static readonly string[] SessionPath = ["session"];

    internal sealed record DelegationExtraction(
        string ParentSessionId,
        string ToolCallId,
        string Title,
        string Status,
        string? ChildSessionId);

    /// <summary>
    /// Maps an <see cref="OpenCodeMessageWithParts"/> to a <see cref="HarnessMessage"/>.
    /// </summary>
    internal static HarnessMessage ToHarnessMessage(OpenCodeMessageWithParts msg)
    {
        var parts = new List<MessagePart>();

        foreach (var part in msg.Parts)
        {
            var mapped = MapPart(part);
            if (mapped is not null)
                parts.Add(mapped);
        }

        return new HarnessMessage
        {
            Id = msg.Info.Id,
            Role = msg.Info.Role,
            Parts = parts,
            Timestamp = DateTimeOffsetFromUnixMs(msg.Info.Time.Created),
            Agent = ExtractAgent(msg.Info),
            ModelId = ExtractModelId(msg.Info),
        };
    }

    /// <summary>
    /// Maps a single <see cref="OpenCodeMessagePart"/> to a Fleet <see cref="MessagePart"/>.
    /// Returns null for unrecognized or unsupported part types.
    /// </summary>
    internal static MessagePart? MapPart(OpenCodeMessagePart part)
    {
        switch (part)
        {
            case OpenCodeTextPart textPart when textPart.Text is not null:
                return new TextPart(textPart.Text);

            case OpenCodeToolPart toolPart:
                var toolState = toolPart.State switch
                {
                    OpenCodeToolPending => ToolUseState.Pending,
                    OpenCodeToolRunning => ToolUseState.Running,
                    OpenCodeToolCompleted => ToolUseState.Completed,
                    OpenCodeToolError => ToolUseState.Error,
                    _ => ToolUseState.Pending,
                };
                return new ToolUsePart(
                    ToolCallId: toolPart.CallId ?? toolPart.Id,
                    ToolName: toolPart.Tool ?? string.Empty,
                    Arguments: toolPart.State switch
                    {
                        OpenCodeToolPending p => p.Input ?? default,
                        OpenCodeToolRunning r => r.Input ?? default,
                        OpenCodeToolCompleted c => c.Input ?? default,
                        OpenCodeToolError e => e.Input ?? default,
                        _ => default,
                    },
                    State: toolState);

            case OpenCodeReasoningPart reasoning when reasoning.Text is not null:
                return new ReasoningPart(reasoning.Text, reasoning.Summary);

            case OpenCodeFilePart filePart when !string.IsNullOrWhiteSpace(filePart.Url):
                return new FilePart(
                    filePart.Id,
                    filePart.Mime ?? string.Empty,
                    filePart.Url,
                    filePart.Filename);

            case OpenCodeStepFinishPart stepFinish:
                return new StepFinishPart(
                    stepFinish.Index,
                    stepFinish.Reason,
                    Cost: 0,
                    TokensInput: 0,
                    TokensOutput: 0,
                    TokensReasoning: 0,
                    CompletedAt: null);

            default:
                return null;
        }
    }

    /// <summary>
    /// Maps a list of <see cref="OpenCodeMessageWithParts"/> to <see cref="HarnessMessage"/> instances.
    /// Messages with an unrecognised role (neither "user" nor "assistant") are silently skipped.
    /// </summary>
    internal static IReadOnlyList<HarnessMessage> ToHarnessMessages(
        IReadOnlyList<OpenCodeMessageWithParts> msgs)
    {
        var result = new List<HarnessMessage>(msgs.Count);
        for (int i = 0; i < msgs.Count; i++)
        {
            var msg = msgs[i];
            // Skip messages whose role discriminator was missing or unrecognised —
            // they deserialised as the base OpenCodeMessageInfo (Role == "unknown").
            if (msg.Info.Role is not ("user" or "assistant"))
                continue;

            result.Add(ToHarnessMessage(msg));
        }
        return result;
    }

    /// <summary>
    /// Maps an <see cref="OpenCodeSseEvent"/> to a <see cref="HarnessEvent"/>.
    /// </summary>
    internal static HarnessEvent ToHarnessEvent(OpenCodeSseEvent evt, string? sessionId)
    {
        var resolvedSession = TryResolveSessionId(evt) ?? sessionId ?? string.Empty;

        return new HarnessEvent
        {
            Type = evt.Type,
            SessionId = resolvedSession,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = evt.Properties,
        };
    }

    /// <summary>
    /// Maps OpenCode agent info to <see cref="HarnessAgent"/> instances.
    /// Agents with a null name are skipped — a nameless agent cannot be meaningfully referenced.
    /// </summary>
    internal static IReadOnlyList<HarnessAgent> ToHarnessAgents(
        IReadOnlyList<OpenCodeAgentInfo> agents)
    {
        var result = new List<HarnessAgent>(agents.Count);
        for (int i = 0; i < agents.Count; i++)
        {
            var a = agents[i];
            if (a.Name is null) continue;
            result.Add(new HarnessAgent(a.Name, a.Description, a.Mode));
        }
        return result;
    }

    /// <summary>
    /// Maps OpenCode providers response to <see cref="HarnessProvider"/> instances.
    /// </summary>
    internal static IReadOnlyList<HarnessProvider> ToHarnessProviders(
        OpenCodeProvidersResponse response)
    {
        var result = new HarnessProvider[response.All.Count];
        for (int i = 0; i < response.All.Count; i++)
        {
            var p = response.All[i];
            var models = p.Models.Values
                .Select(m => new HarnessModel(m.Id, m.Name ?? m.Id))
                .ToList();
            result[i] = new HarnessProvider(p.Id, p.Name ?? p.Id, models);
        }
        return result;
    }

    /// <summary>Converts a Unix millisecond timestamp to <see cref="DateTimeOffset"/>.</summary>
    internal static DateTimeOffset DateTimeOffsetFromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    /// <summary>
    /// Extracts the agent name from a polymorphic <see cref="OpenCodeMessageInfo"/> subtype.
    /// Returns <c>null</c> for base-type messages that lack an agent field.
    /// </summary>
    private static string? ExtractAgent(OpenCodeMessageInfo info) => info switch
    {
        OpenCodeAssistantMessage a => a.Agent,
        OpenCodeUserMessage u => u.Agent,
        _ => null,
    };

    private static string? ExtractModelId(OpenCodeMessageInfo info) => info switch
    {
        OpenCodeAssistantMessage a => a.ModelId,
        _ => null,
    };

    /// <summary>
    /// Attempts to extract token/cost data from an OpenCode SSE event.
    /// Only processes <c>message.updated</c> events where the Properties contains
    /// an <c>"info"</c> field wrapping an assistant message with token data.
    /// Returns <c>null</c> for all other events, or on any parse failure.
    /// This is a side-channel extraction — it must never throw or block.
    /// </summary>
    /// <remarks>
    /// The <c>message.created</c> event type is intentionally excluded.
    /// Its Properties structure has not been verified against a contract fixture,
    /// so it is unsafe to deserialize. See analytics-store.md § Task 14 for details.
    /// </remarks>
    internal static TokenEventData? TryExtractTokenEvent(
        OpenCodeSseEvent evt,
        string? sessionId,
        string? projectId,
        string? projectName,
        string? workspaceDirectory,
        string userId = "local-user")
    {
        // Only process message.updated events
        if (evt.Type is not EventTypes.MessageUpdated)
            return null;

        try
        {
            if (evt.Properties.ValueKind != JsonValueKind.Object)
                return null;

            // The message.updated Properties wraps the message under "info":
            //   { "info": { "id": "...", "role": "assistant", ... } }
            // Confirmed by contract fixture: tests/contracts/opencode-to-fleet-events.json
            if (!evt.Properties.TryGetProperty("info", out var infoEl))
                return null;

            // Check "role" directly — avoid polymorphic deserialization of the abstract base type,
            // which requires the discriminator to be the first property in some STJ configurations.
            if (!infoEl.TryGetProperty("role", out var roleEl)
                || roleEl.GetString() is not "assistant")
                return null;

            var assistant = OpenCodeMessageDeserializer.DeserializeAssistantMessage(infoEl);
            if (assistant is null)
                return null;

            // Skip messages with no token data
            if (assistant.Tokens is null && assistant.Cost is null)
                return null;

            var tokens = assistant.Tokens;
            var tokensTotal = tokens?.Total ?? 0;
            var cost = assistant.Cost ?? 0;

            // Skip events with no meaningful data — reduces unnecessary writes
            if (tokensTotal == 0 && cost == 0)
                return null;

            var estimatedCost = ModelPricing.EstimateCost(
                assistant.ModelId,
                tokens?.Input ?? 0,
                tokens?.Output ?? 0,
                tokens?.Reasoning ?? 0,
                tokens?.Cache?.Read ?? 0);

            return new TokenEventData(
                EventId: $"{sessionId}:{assistant.Id}",
                SessionId: sessionId ?? "",
                ProjectId: projectId,
                ProjectName: projectName,
                WorkspaceDirectory: workspaceDirectory,
                ModelId: assistant.ModelId,
                ProviderId: assistant.ProviderId,
                TokensInput: tokens?.Input ?? 0,
                TokensOutput: tokens?.Output ?? 0,
                TokensReasoning: tokens?.Reasoning ?? 0,
                TokensCacheRead: tokens?.Cache?.Read ?? 0,
                TokensCacheWrite: tokens?.Cache?.Write ?? 0,
                TokensTotal: tokensTotal,
                Cost: cost,
                EstimatedCost: estimatedCost,
                CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(assistant.Time.Created),
                UserId: userId);
        }
        catch
        {
            // Silent failure — analytics must never crash sessions
            return null;
        }
    }

    internal static TokenEventData WithModelInfo(
        TokenEventData tokenEvent,
        string? modelId,
        string? providerId)
    {
        var resolvedModelId = string.IsNullOrWhiteSpace(modelId)
            ? tokenEvent.ModelId
            : modelId;

        var resolvedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? tokenEvent.ProviderId
            : providerId;

        if (resolvedProviderId is null && !string.IsNullOrWhiteSpace(resolvedModelId))
        {
            var slash = resolvedModelId.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0)
                resolvedProviderId = resolvedModelId[..slash];
        }

        return tokenEvent with
        {
            ModelId = resolvedModelId,
            ProviderId = resolvedProviderId,
            EstimatedCost = ModelPricing.EstimateCost(
                resolvedModelId,
                tokenEvent.TokensInput,
                tokenEvent.TokensOutput,
                tokenEvent.TokensReasoning,
                tokenEvent.TokensCacheRead)
        };
    }

    internal static DelegationExtraction? TryExtractDelegation(OpenCodeSseEvent evt, string parentSessionId)
    {
        if (evt.Type is not EventTypes.MessagePartUpdated)
            return null;

        if (string.IsNullOrWhiteSpace(parentSessionId))
            return null;

        try
        {
            if (evt.Properties.ValueKind != JsonValueKind.Object)
                return null;

            if (!evt.Properties.TryGetProperty("part", out var partEl) || partEl.ValueKind != JsonValueKind.Object)
                return null;

            if (TryExtractSubtaskDelegation(partEl, parentSessionId) is { } subtaskExtraction)
                return subtaskExtraction;

            if (!HasStringProperty(partEl, "type", out var partType) || partType != "tool")
                return null;

            if (!HasStringProperty(partEl, "tool", out var toolName) || toolName != "task")
                return null;

            if (!TryGetStringProperty(partEl, out var toolCallId, "callID", "callId")
                || string.IsNullOrWhiteSpace(toolCallId))
                return null;

            if (!partEl.TryGetProperty("state", out var stateEl) || stateEl.ValueKind != JsonValueKind.Object)
                return null;

            if (!HasStringProperty(stateEl, "status", out var status)
                || status is not ("pending" or "running" or "completed" or "error" or "cancelled"))
                return null;

            if (!stateEl.TryGetProperty("input", out var inputEl) || inputEl.ValueKind != JsonValueKind.Object)
                return null;

            if (!TryGetStringProperty(inputEl, out var title, "subagent_type", "agent")
                || string.IsNullOrWhiteSpace(title))
                return null;

            string? childSessionId = null;
            if (stateEl.TryGetProperty("metadata", out var metadataEl) && metadataEl.ValueKind == JsonValueKind.Object)
            {
                _ = TryGetStringProperty(metadataEl, out childSessionId, "sessionId", "sessionID", "session_id");

                if (string.IsNullOrWhiteSpace(childSessionId)
                    && TryGetNestedStringProperty(metadataEl, out var nestedChildSessionId, ChildPath, "sessionId", "sessionID", "session_id"))
                {
                    childSessionId = nestedChildSessionId;
                }

                if (string.IsNullOrWhiteSpace(childSessionId)
                    && TryGetNestedStringProperty(metadataEl, out var nestedSessionChildSessionId, SessionPath, "sessionId", "sessionID", "session_id"))
                {
                    childSessionId = nestedSessionChildSessionId;
                }
            }

            return new DelegationExtraction(parentSessionId, toolCallId, title, status, childSessionId);
        }
        catch
        {
            return null;
        }
    }

    private static DelegationExtraction? TryExtractSubtaskDelegation(JsonElement partEl, string parentSessionId)
    {
        if (!HasStringProperty(partEl, "type", out var partType) || partType != "subtask")
            return null;

        if (!TryGetStringProperty(partEl, out var toolCallId, "callId", "callID")
            || string.IsNullOrWhiteSpace(toolCallId))
        {
            return null;
        }

        if (!TryGetStringProperty(partEl, out var title, "agent", "description")
            || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string? childSessionId = null;
        if (partEl.TryGetProperty("metadata", out var metadataEl) && metadataEl.ValueKind == JsonValueKind.Object)
        {
            _ = TryGetStringProperty(metadataEl, out childSessionId, "sessionId", "sessionID", "session_id");

            if (string.IsNullOrWhiteSpace(childSessionId)
                && TryGetNestedStringProperty(metadataEl, out var nestedChildSessionId, ChildPath, "sessionId", "sessionID", "session_id"))
            {
                childSessionId = nestedChildSessionId;
            }

            if (string.IsNullOrWhiteSpace(childSessionId)
                && TryGetNestedStringProperty(metadataEl, out var nestedSessionChildSessionId, SessionPath, "sessionId", "sessionID", "session_id"))
            {
                childSessionId = nestedSessionChildSessionId;
            }
        }

        var status = string.IsNullOrWhiteSpace(childSessionId) ? "pending" : "running";
        return new DelegationExtraction(parentSessionId, toolCallId, title, status, childSessionId);
    }

    internal static string? TryResolveSessionId(OpenCodeSseEvent evt)
    {
        if (evt.Properties.ValueKind != JsonValueKind.Object)
            return null;

        if (TryGetStringProperty(evt.Properties, out var topLevelSessionId, "sessionId", "sessionID", "session_id")
            && !string.IsNullOrWhiteSpace(topLevelSessionId))
        {
            return topLevelSessionId;
        }

        if (TryGetNestedStringProperty(evt.Properties, out var infoSessionId, InfoPath, "sessionId", "sessionID", "session_id")
            && !string.IsNullOrWhiteSpace(infoSessionId))
        {
            return infoSessionId;
        }

        if (TryGetNestedStringProperty(evt.Properties, out var partSessionId, PartPath, "sessionId", "sessionID", "session_id")
            && !string.IsNullOrWhiteSpace(partSessionId))
        {
            return partSessionId;
        }

        return null;
    }

    private static bool HasStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetStringProperty(JsonElement element, out string? value, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                continue;

            value = property.GetString();
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetNestedStringProperty(
        JsonElement element,
        out string? value,
        string[] objectPath,
        params string[] propertyNames)
    {
        var current = element;
        for (int i = 0; i < objectPath.Length; i++)
        {
            if (!current.TryGetProperty(objectPath[i], out var next) || next.ValueKind != JsonValueKind.Object)
            {
                value = null;
                return false;
            }

            current = next;
        }

        return TryGetStringProperty(current, out value, propertyNames);
    }
}
