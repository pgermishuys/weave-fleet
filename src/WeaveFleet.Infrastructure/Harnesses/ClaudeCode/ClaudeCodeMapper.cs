using System.Text.Json;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>Maps Claude Code DTOs to Weave Fleet domain types.</summary>
internal static class ClaudeCodeMapper
{
    /// <summary>
    /// Maps a <see cref="ClaudeCodeAssistantMessage"/> to a <see cref="HarnessMessage"/>.
    /// Content blocks map as:
    ///   "text"        → <see cref="TextPart"/>
    ///   "tool_use"    → <see cref="ToolUsePart"/> (State=Running)
    ///   "tool_result" → <see cref="ToolResultPart"/>
    /// </summary>
    internal static HarnessMessage ToHarnessMessage(
        ClaudeCodeAssistantMessage msg, DateTimeOffset timestamp)
    {
        var parts = new List<MessagePart>();

        if (msg.Message?.Content is not null)
        {
            foreach (var block in msg.Message.Content)
            {
                var part = ToMessagePart(block);
                if (part is not null)
                    parts.Add(part);
            }
        }

        return new HarnessMessage
        {
            Id = msg.Message?.Id ?? $"assistant-{Guid.NewGuid():N}",
            Role = "assistant",
            Parts = parts,
            Timestamp = timestamp,
        };
    }

    /// <summary>
    /// Creates a synthetic user <see cref="HarnessMessage"/> for prompt tracking.
    /// Claude Code doesn't echo back user messages, so they are created synthetically
    /// when the user sends a prompt.
    /// </summary>
    internal static HarnessMessage ToUserMessage(string prompt, DateTimeOffset timestamp)
    {
        return new HarnessMessage
        {
            Id = $"user-{Guid.NewGuid():N}",
            Role = "user",
            Parts = [new TextPart(prompt)],
            Timestamp = timestamp,
        };
    }

    /// <summary>
    /// Creates frontend-compatible events from a Claude Code stream message.
    /// Maps to the event types the frontend handles: <c>message.updated</c>,
    /// <c>message.part.updated</c>, and <c>session.idle</c>.
    /// System/init messages do not produce frontend events.
    /// </summary>
    internal static IReadOnlyList<HarnessEvent> ToFrontendEvents(
        ClaudeCodeStreamMessage msg, string sessionId)
    {
        return msg switch
        {
            ClaudeCodeAssistantMessage assistant => CreateAssistantEvents(assistant, sessionId),
            ClaudeCodeResultMessage => [CreateSessionIdleEvent(sessionId)],
            _ => [], // system/init messages don't produce frontend events
        };
    }

    /// <summary>
    /// Creates a <c>message.updated</c> event with the payload structure the frontend expects:
    /// <c>{ info: { id, role, sessionID, time: { created } } }</c>.
    /// </summary>
    internal static HarnessEvent CreateMessageUpdatedEvent(HarnessMessage msg, string sessionId)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = msg.Id,
                role = msg.Role,
                sessionID = sessionId,
                time = new { created = msg.Timestamp.ToUnixTimeMilliseconds() },
            }
        });

        return new HarnessEvent
        {
            Type = "message.updated",
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        };
    }

    /// <summary>
    /// Creates a <c>message.part.updated</c> event for a single message part.
    /// Returns <c>null</c> for unrecognised part types (e.g. tool results).
    /// </summary>
    internal static HarnessEvent? CreatePartUpdatedEvent(
        string messageId, string sessionId, MessagePart part, int partIndex)
    {
        object? partPayload = part switch
        {
            TextPart text => new
            {
                messageID = messageId,
                sessionID = sessionId,
                type = "text",
                id = $"{messageId}-part-{partIndex}",
                text = text.Text,
            },
            ToolUsePart tool => BuildToolPartPayload(messageId, sessionId, tool, partIndex),
            _ => null,
        };

        if (partPayload is null) return null;

        var payload = JsonSerializer.SerializeToElement(new { part = partPayload });
        return new HarnessEvent
        {
            Type = "message.part.updated",
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        };
    }

    /// <summary>
    /// Creates a <c>session.status</c> event with the given status type (e.g. "busy" or "idle").
    /// </summary>
    internal static HarnessEvent CreateSessionStatusEvent(string sessionId, string statusType)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            status = new { type = statusType }
        });

        return new HarnessEvent
        {
            Type = "session.status",
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        };
    }

    private static List<HarnessEvent> CreateAssistantEvents(
        ClaudeCodeAssistantMessage assistant, string sessionId)
    {
        var harnessMsg = ToHarnessMessage(assistant, DateTimeOffset.UtcNow);
        var events = new List<HarnessEvent>(1 + harnessMsg.Parts.Count);

        events.Add(CreateMessageUpdatedEvent(harnessMsg, sessionId));

        for (int i = 0; i < harnessMsg.Parts.Count; i++)
        {
            var partEvent = CreatePartUpdatedEvent(harnessMsg.Id, sessionId, harnessMsg.Parts[i], i);
            if (partEvent is not null)
                events.Add(partEvent);
        }

        return events;
    }

    private static object BuildToolPartPayload(
        string messageId, string sessionId, ToolUsePart tool, int partIndex)
    {
        var state = new Dictionary<string, object>
        {
            ["status"] = MapToolUseState(tool.State),
        };

        if (tool.Arguments.ValueKind != JsonValueKind.Undefined)
        {
            state["input"] = tool.Arguments;
        }

        return new
        {
            messageID = messageId,
            sessionID = sessionId,
            type = "tool",
            id = $"{messageId}-part-{partIndex}",
            tool = tool.ToolName,
            callID = tool.ToolCallId,
            state,
        };
    }

    private static string MapToolUseState(ToolUseState state) => state switch
    {
        ToolUseState.Pending => "pending",
        ToolUseState.Running => "running",
        ToolUseState.Completed => "completed",
        ToolUseState.Error => "error",
        _ => "pending",
    };

    private static HarnessEvent CreateSessionIdleEvent(string sessionId)
    {
        return new HarnessEvent
        {
            Type = "session.idle",
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = null,
        };
    }

    /// <summary>
    /// Maps a single <see cref="ClaudeCodeContentBlock"/> to a domain <see cref="MessagePart"/>.
    /// Returns null for unrecognized block types.
    /// </summary>
    internal static MessagePart? ToMessagePart(ClaudeCodeContentBlock block)
    {
        return block switch
        {
            ClaudeCodeTextBlock text => text.Text is not null
                ? new TextPart(text.Text)
                : null,

            ClaudeCodeToolUseBlock toolUse => new ToolUsePart(
                ToolCallId: toolUse.Id ?? string.Empty,
                ToolName: toolUse.Name ?? string.Empty,
                Arguments: toolUse.Input,
                // Claude Code doesn't distinguish pending/completed during streaming
                State: ToolUseState.Running),

            ClaudeCodeToolResultBlock toolResult => new ToolResultPart(
                ToolCallId: toolResult.ToolUseId ?? string.Empty,
                Content: toolResult.Content ?? string.Empty,
                IsError: toolResult.IsError ?? false),

            _ => null,
        };
    }

    /// <summary>
    /// Extracts token/cost analytics from a result message.
    /// Equivalent to <c>OpenCodeMapper.TryExtractTokenEvent</c>.
    /// Called from <c>ClaudeCodeHarnessInstance</c> when a result message arrives.
    /// Returns null if no cost/usage data or on any parse failure.
    /// </summary>
    internal static TokenEventData? TryExtractTokenEvent(
        ClaudeCodeResultMessage result,
        string? sessionId,
        string? projectId,
        string? projectName,
        string? workspaceDirectory,
        string? modelId)
    {
        try
        {
            // Skip if there's no usage or cost data
            if (result.Usage is null && result.TotalCostUsd is null)
                return null;

            var usage = result.Usage;
            var inputTokens = (double)(usage?.InputTokens ?? 0);
            var outputTokens = (double)(usage?.OutputTokens ?? 0);
            var cacheReadTokens = (double)(usage?.CacheReadInputTokens ?? 0);
            var totalCostUsd = result.TotalCostUsd.HasValue
                ? (double)result.TotalCostUsd.Value
                : 0.0;

            var estimatedCost = ModelPricing.EstimateCost(
                modelId,
                inputTokens,
                outputTokens,
                reasoningTokens: 0,
                cacheReadTokens: cacheReadTokens);

            var resolvedSessionId = result.SessionId ?? sessionId ?? string.Empty;

            return new TokenEventData(
                EventId: $"{resolvedSessionId}:{result.GetHashCode()}",
                SessionId: resolvedSessionId,
                ProjectId: projectId,
                ProjectName: projectName,
                WorkspaceDirectory: workspaceDirectory,
                ModelId: modelId,
                ProviderId: "anthropic",
                TokensInput: inputTokens,
                TokensOutput: outputTokens,
                TokensReasoning: 0,
                TokensCacheRead: cacheReadTokens,
                TokensCacheWrite: (double)(usage?.CacheCreationInputTokens ?? 0),
                TokensTotal: inputTokens + outputTokens,
                Cost: totalCostUsd,
                EstimatedCost: estimatedCost,
                CreatedAt: DateTimeOffset.UtcNow);
        }
        catch
        {
            // Silent failure — analytics must never crash sessions
            return null;
        }
    }
}
