using System.Text.Json;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>Maps Claude Code DTOs to Weave Fleet domain types.</summary>
internal static class ClaudeCodeMapper
{
    /// <summary>
    /// Maps a <see cref="ClaudeCodeAssistantMessage"/> to a <see cref="HarnessMessage"/>.
    /// Content blocks map as:
    ///   "text"        → <see cref="TextPart"/>
    ///   "thinking"    → <see cref="ReasoningPart"/> (only when it has text)
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
            ModelId = msg.Message?.Model,
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
        var payload = JsonSerializer.SerializeToElement(
            new ClaudeCodeMessageUpdatedPayload
            {
                Info = new ClaudeCodeMapperInfo
                {
                    Id = msg.Id,
                    Role = msg.Role,
                    SessionID = sessionId,
                    ModelID = msg.ModelId,
                    Time = new ClaudeCodeMapperInfoTime { Created = msg.Timestamp.ToUnixTimeMilliseconds() },
                }
            },
            InfrastructureJsonContext.Default.ClaudeCodeMessageUpdatedPayload);

        return new HarnessEvent
        {
            Type = EventTypes.MessageUpdated,
            SessionId = sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        };
    }

    /// <summary>
    /// Creates a <c>message.part.updated</c> event for a single message part.
    /// Returns <c>null</c> for unrecognised part types.
    /// </summary>
    /// <param name="toolOutput">For a finished <see cref="ToolUsePart"/>, what the tool returned.</param>
    internal static HarnessEvent? CreatePartUpdatedEvent(
        string messageId, string sessionId, MessagePart part, int partIndex, string? toolOutput = null)
    {
        JsonElement? payload = part switch
        {
            TextPart text => SerializeTextPart(messageId, sessionId, "text", text.PartId ?? $"{messageId}-part-{partIndex}", text.Text),
            ReasoningPart reasoning => SerializeTextPart(messageId, sessionId, "reasoning", reasoning.PartId ?? $"{messageId}-part-{partIndex}", reasoning.Text),
            ToolUsePart tool => JsonSerializer.SerializeToElement(
                BuildToolPartPayload(messageId, sessionId, tool, partIndex, toolOutput),
                InfrastructureJsonContext.Default.ClaudeCodeToolPartPayload),
            ToolResultPart toolResult => ToolResultEventBuilder.BuildPayload(
                messageId,
                sessionId,
                toolResult.ToolCallId,
                toolResult.Content,
                toolResult.IsError),
            _ => null,
        };

        if (payload is null) return null;
        return new HarnessEvent
        {
            Type = EventTypes.MessagePartUpdated,
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
        var payload = JsonSerializer.SerializeToElement(
            new SessionStatusEventPayload
            {
                Status = new SessionStatusEventKind { Type = statusType }
            },
            InfrastructureJsonContext.Default.SessionStatusEventPayload);

        return new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
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

    private static JsonElement SerializeTextPart(string messageId, string sessionId, string type, string partId, string text)
        => JsonSerializer.SerializeToElement(
            new ClaudeCodeTextPartPayload
            {
                Part = new ClaudeCodeTextPartContent
                {
                    MessageID = messageId,
                    SessionID = sessionId,
                    Type = type,
                    Id = partId,
                    Text = text,
                }
            },
            InfrastructureJsonContext.Default.ClaudeCodeTextPartPayload);

    private static ClaudeCodeToolPartPayload BuildToolPartPayload(
        string messageId, string sessionId, ToolUsePart tool, int partIndex, string? toolOutput)
    {
        var state = new ClaudeCodeToolStateContent
        {
            Status = MapToolUseState(tool.State),
            Input = tool.Arguments.ValueKind != JsonValueKind.Undefined ? tool.Arguments : null,
            Output = toolOutput is null ? null : ToToolOutput(toolOutput),
            Error = tool.Error,
        };

        return new ClaudeCodeToolPartPayload
        {
            Part = new ClaudeCodeToolPartContent
            {
                MessageID = messageId,
                SessionID = sessionId,
                Type = "tool",
                Id = tool.PartId ?? $"{messageId}-part-{partIndex}",
                Tool = tool.ToolName,
                CallID = tool.ToolCallId,
                State = state,
            }
        };
    }

    /// <summary>A tool's output as JSON when it is JSON, otherwise as a string, the way a reloaded session reads it.</summary>
    private static JsonElement ToToolOutput(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(content, InfrastructureJsonContext.Default.String);
        }
    }

    private static string MapToolUseState(ToolUseState state) => state switch
    {
        ToolUseState.Pending => "pending",
        ToolUseState.Running => "running",
        ToolUseState.Completed => "completed",
        ToolUseState.Error => "error",
        _ => "pending",
    };

    internal static HarnessEvent CreateSessionIdleEvent(string sessionId)
    {
        return new HarnessEvent
        {
            Type = EventTypes.SessionIdle,
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

            ClaudeCodeThinkingBlock thinking => !string.IsNullOrEmpty(thinking.Thinking)
                ? new ReasoningPart(thinking.Thinking)
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
    /// Why a run failed, from its result line (e.g. "Reached maximum number of turns (1)"),
    /// or null when it succeeded.
    /// </summary>
    internal static string? DescribeFailedResult(ClaudeCodeResultMessage result)
    {
        if (result.IsError != true && result.Subtype is null or "success")
            return null;

        if (result.Errors is { Count: > 0 } errors)
            return string.Join("\n", errors);

        return !string.IsNullOrWhiteSpace(result.Result)
            ? result.Result.Trim()
            : result.Subtype ?? "unknown error";
    }

    /// <summary>
    /// Extracts token/cost analytics from a result message.
    /// Equivalent to <c>OpenCodeMapper.TryExtractTokenEvent</c>.
    /// Called from <c>ClaudeCodeHarnessSession</c> when a result message arrives.
    /// Returns null if no cost/usage data or on any parse failure.
    /// </summary>
    internal static TokenEventData? TryExtractTokenEvent(
        ClaudeCodeResultMessage result,
        string? sessionId,
        string? projectId,
        string? projectName,
        string? workspaceDirectory,
        string? modelId,
        string userId = "local-user")
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
                CreatedAt: DateTimeOffset.UtcNow,
                UserId: userId);
        }
        catch
        {
            // Silent failure — analytics must never crash sessions
            return null;
        }
    }
}
