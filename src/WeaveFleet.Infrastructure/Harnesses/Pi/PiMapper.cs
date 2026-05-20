using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary>
/// Maps Pi JSONL events to Fleet harness events that satisfy the harness-agnostic
/// domain event contract consumed by the frontend reducer.
/// </summary>
internal sealed class PiMapper
{
    private const string AssistantRole = "assistant";
    private const string UserRole = "user";
    private const string TextField = "text";

    private readonly string _sessionId;
    private readonly string? _agent;
    private readonly Dictionary<int, string> _contentPartText = [];
    private readonly Dictionary<int, string> _contentPartReasoning = [];
    private readonly Dictionary<string, PiToolPartState> _toolsByCallId = [];

    private int _messageSequence;
    private int _turnIndex;
    private string? _activeMessageId;
    private string? _activeAssistantMessageId;
    private string? _lastAssistantMessageId;

    /// <summary>Creates a mapper for a Fleet session.</summary>
    internal PiMapper(string sessionId)
        : this(sessionId, null)
    {
    }

    /// <summary>Creates a mapper for a Fleet session and selected agent.</summary>
    internal PiMapper(string sessionId, string? agent)
    {
        _sessionId = sessionId;
        _agent = agent;
    }

    /// <summary>Stateless convenience mapper for tests and one-off events.</summary>
    internal static IReadOnlyList<HarnessEvent> ToHarnessEvents(PiEvent evt, string sessionId)
        => new PiMapper(sessionId).Map(evt);

    /// <summary>Maps one Pi event into zero or more Fleet harness events.</summary>
    internal IReadOnlyList<HarnessEvent> Map(PiEvent evt)
    {
        return evt switch
        {
            PiAgentStartEvent => [CreateStatusEvent("busy")],
            PiAgentEndEvent agentEnd => MapAgentEnd(agentEnd),
            PiTurnStartEvent => [CreateStatusEvent("busy")],
            PiTurnEndEvent turnEnd => MapTurnEnd(turnEnd),
            PiMessageStartEvent messageStart => MapMessageStart(messageStart),
            PiMessageUpdateEvent messageUpdate => MapMessageUpdate(messageUpdate),
            PiMessageEndEvent messageEnd => MapMessageEnd(messageEnd),
            PiToolExecutionStartEvent toolStart => MapToolExecutionStart(toolStart),
            PiToolExecutionUpdateEvent toolUpdate => MapToolExecutionUpdate(toolUpdate),
            PiToolExecutionEndEvent toolEnd => MapToolExecutionEnd(toolEnd),
            PiCompactionStartEvent compactionStart => [CreateInformationalStatus("working", "compaction_start", compactionStart.Message)],
            PiCompactionEndEvent compactionEnd => [CreateCompactedEvent(compactionEnd)],
            PiAutoRetryStartEvent retryStart => [CreateInformationalStatus("working", "auto_retry_start", retryStart.Reason)],
            PiAutoRetryEndEvent retryEnd => [CreateInformationalStatus("busy", "auto_retry_end", retryEnd.Success?.ToString(CultureInfo.InvariantCulture))],
            PiQueueUpdateEvent => [],
            PiIdleEvent => [CreateStatusEvent("idle")],
            PiErrorEvent error => [CreateErrorEvent(error.Message ?? error.Error ?? "Pi emitted an error.")],
            PiProtocolErrorEvent protocolError => [CreateErrorEvent(protocolError.Message)],
            PiResponseEvent response => MapResponse(response),
            PiLogEvent => [],
            PiSessionSwitchedEvent sessionSwitched => [CreateSessionUpdatedEvent(sessionSwitched)],
            PiStateUpdateEvent stateUpdate => [CreateSessionUpdatedEvent(stateUpdate)],
            _ => [],
        };
    }

    private List<HarnessEvent> MapAgentEnd(PiAgentEndEvent evt)
    {
        var events = new List<HarnessEvent>(evt.Messages.Count + 1);
        foreach (var message in evt.Messages)
        {
            if (ShouldSuppressMessage(message))
                continue;

            events.Add(CreateMessageLifecycleEvent(EventTypes.MessageUpdated, message, completed: true));
        }

        events.Add(CreateStatusEvent("idle"));
        return events;
    }

    private List<HarnessEvent> MapTurnEnd(PiTurnEndEvent evt)
    {
        var events = new List<HarnessEvent>(2);
        if (evt.Message is not null && !ShouldSuppressMessage(evt.Message))
        {
            events.Add(CreateMessageLifecycleEvent(EventTypes.MessageUpdated, evt.Message, completed: true));
            if (IsAssistant(evt.Message))
                events.Add(CreateStepFinishEvent(evt.Message));
        }

        events.Add(CreateStatusEvent("idle", evt.Message));
        return events;
    }

    private IReadOnlyList<HarnessEvent> MapMessageStart(PiMessageStartEvent evt)
    {
        if (ShouldSuppressMessage(evt.Message))
            return [];

        var messageId = ResolveMessageId(evt.Message);
        _activeMessageId = messageId;
        if (IsAssistant(evt.Message))
        {
            _activeAssistantMessageId = messageId;
            _lastAssistantMessageId = messageId;
            _contentPartText.Clear();
            _contentPartReasoning.Clear();
        }

        return [CreateMessageLifecycleEvent(EventTypes.MessageCreated, evt.Message, completed: false)];
    }

    private IReadOnlyList<HarnessEvent> MapMessageUpdate(PiMessageUpdateEvent evt)
    {
        if (evt.AssistantMessageEvent is null)
        {
            return evt.Message is null || ShouldSuppressMessage(evt.Message)
                ? []
                : [CreateMessageLifecycleEvent(EventTypes.MessageUpdated, evt.Message, completed: false)];
        }

        var message = evt.AssistantMessageEvent.Partial ?? evt.Message;
        var messageId = message is null ? ResolveActiveAssistantMessageId() : ResolveMessageId(message);
        _activeAssistantMessageId = messageId;
        _lastAssistantMessageId = messageId;

        return evt.AssistantMessageEvent switch
        {
            PiTextStartEvent textStart => [CreateTextPartUpdatedEvent(messageId, textStart.ContentIndex, CurrentText(textStart.ContentIndex))],
            PiTextDeltaEvent textDelta => MapTextDelta(messageId, textDelta, message),
            PiTextEndEvent textEnd => [CreateTextPartUpdatedEvent(messageId, textEnd.ContentIndex, FinalText(textEnd.ContentIndex, textEnd.Content, message))],
            PiThinkingStartEvent thinkingStart => [CreateReasoningPartUpdatedEvent(messageId, thinkingStart.ContentIndex, CurrentReasoning(thinkingStart.ContentIndex))],
            PiThinkingDeltaEvent thinkingDelta => MapThinkingDelta(messageId, thinkingDelta, message),
            PiThinkingEndEvent thinkingEnd => [CreateReasoningPartUpdatedEvent(messageId, thinkingEnd.ContentIndex, FinalReasoning(thinkingEnd.ContentIndex, thinkingEnd.Content, message))],
            PiToolCallStartEvent toolStart => [CreatePendingToolPartUpdatedEvent(messageId, toolStart.ContentIndex, null)],
            PiToolCallDeltaEvent toolDelta => [CreatePendingToolPartUpdatedEvent(messageId, toolDelta.ContentIndex, TryParseJson(toolDelta.Delta))],
            PiToolCallEndEvent toolEnd when toolEnd.ToolCall is not null => [CreateToolCallPartUpdatedEvent(messageId, toolEnd.ContentIndex, toolEnd.ToolCall)],
            _ => message is null || ShouldSuppressMessage(message)
                ? []
                : [CreateMessageLifecycleEvent(EventTypes.MessageUpdated, message, completed: false)],
        };
    }

    private List<HarnessEvent> MapMessageEnd(PiMessageEndEvent evt)
    {
        if (ShouldSuppressMessage(evt.Message))
            return [];

        var events = new List<HarnessEvent>(3)
        {
            CreateMessageLifecycleEvent(EventTypes.MessageUpdated, evt.Message, completed: true)
        };

        if (IsAssistant(evt.Message))
        {
            events.Add(CreateStepFinishEvent(evt.Message));
            _lastAssistantMessageId = ResolveMessageId(evt.Message);
        }

        if (IsAborted(evt.Message))
            events.Add(CreateSessionErrorEvent(evt.Message.ErrorMessage ?? "Pi assistant message was aborted."));

        _activeMessageId = null;
        return events;
    }

    private IReadOnlyList<HarnessEvent> MapTextDelta(string messageId, PiTextDeltaEvent evt, PiMessage? message)
    {
        var delta = evt.Delta ?? string.Empty;
        if (delta.Length == 0)
        {
            var text = TextAtContentIndex(message, evt.ContentIndex);
            return text is null ? [] : [CreateTextPartUpdatedEvent(messageId, evt.ContentIndex, text)];
        }

        _contentPartText[evt.ContentIndex] = CurrentText(evt.ContentIndex) + delta;
        return [CreateTextPartDeltaEvent(messageId, ContentPartId(messageId, evt.ContentIndex), delta)];
    }

    private IReadOnlyList<HarnessEvent> MapThinkingDelta(string messageId, PiThinkingDeltaEvent evt, PiMessage? message)
    {
        var delta = evt.Delta ?? string.Empty;
        if (delta.Length == 0)
        {
            var reasoning = ReasoningAtContentIndex(message, evt.ContentIndex);
            return reasoning is null ? [] : [CreateReasoningPartUpdatedEvent(messageId, evt.ContentIndex, reasoning)];
        }

        var text = CurrentReasoning(evt.ContentIndex) + delta;
        _contentPartReasoning[evt.ContentIndex] = text;
        return [CreateReasoningPartUpdatedEvent(messageId, evt.ContentIndex, text)];
    }

    private IReadOnlyList<HarnessEvent> MapToolExecutionStart(PiToolExecutionStartEvent evt)
    {
        var messageId = ResolveToolHostMessageId(evt.ToolCallId);
        var state = new PiToolPartState(messageId, ToolPartId(messageId, evt.ToolCallId), evt.ToolCallId, evt.ToolName, Clone(evt.Args), null, null);
        _toolsByCallId[evt.ToolCallId] = state;
        return [CreateToolPartUpdatedEvent(state, new ToolRunningState { Input = state.Input })];
    }

    private IReadOnlyList<HarnessEvent> MapToolExecutionUpdate(PiToolExecutionUpdateEvent evt)
    {
        var state = UpsertToolState(evt.ToolCallId, evt.ToolName, evt.Args);
        var output = ToolResultToJson(evt.PartialResult);
        _toolsByCallId[evt.ToolCallId] = state with { Output = output };
        return [CreateToolPartUpdatedEvent(state, new ToolRunningState { Input = state.Input })];
    }

    private IReadOnlyList<HarnessEvent> MapToolExecutionEnd(PiToolExecutionEndEvent evt)
    {
        var state = UpsertToolState(evt.ToolCallId, evt.ToolName, null);
        var output = ToolResultToJson(evt.Result);
        var finalState = state with { Output = output, IsError = evt.IsError };
        _toolsByCallId[evt.ToolCallId] = finalState;

        ToolInvocationState invocationState = evt.IsError
            ? new ToolErrorState { Input = finalState.Input, Output = finalState.Output }
            : new ToolCompletedState { Input = finalState.Input, Output = finalState.Output, Metadata = ToolResultDetails(evt.Result) };

        return [CreateToolPartUpdatedEvent(finalState, invocationState)];
    }

    private IReadOnlyList<HarnessEvent> MapResponse(PiResponseEvent evt)
    {
        if (!evt.Success)
            return [CreateErrorEvent(evt.Error ?? $"Pi command failed: {evt.Command}")];

        if (evt.Command is "get_state" && evt.Data is { ValueKind: JsonValueKind.Object })
            return [CreateRawPayloadEvent(EventTypes.SessionUpdated, evt.Data.Value.Clone())];

        return [];
    }

    private HarnessEvent CreateMessageLifecycleEvent(string type, PiMessage message, bool completed)
    {
        var messageId = ResolveMessageId(message);
        var created = message.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = new MessageLifecyclePayload
        {
            Info = new MessageEventInfo
            {
                Id = messageId,
                Role = ToFleetRole(message.Role),
                SessionId = _sessionId,
                Agent = AgentForMessage(message),
                ModelId = message.ResponseModel ?? message.Model,
                ParentId = null,
                Time = new MessageEventTime
                {
                    Created = created,
                    Completed = completed ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null,
                },
                Cost = message.Usage?.Cost?.Total,
                Tokens = message.Usage is null
                    ? null
                    : new MessageTokenUsage
                    {
                        Input = message.Usage.Input,
                        Output = message.Usage.Output,
                        Reasoning = 0,
                    },
            },
            Parts = MapContentBlocks(message, messageId),
        };

        return CreateEvent(type, JsonSerializer.SerializeToElement(payload, InfrastructureJsonContext.Default.MessageLifecyclePayload));
    }

    private HarnessEvent CreateTextPartUpdatedEvent(string messageId, int contentIndex, string text)
    {
        _contentPartText[contentIndex] = text;
        return CreatePartUpdatedEvent(new TextMessageEventPart
        {
            Id = ContentPartId(messageId, contentIndex),
            SessionId = _sessionId,
            MessageId = messageId,
            Text = text,
        });
    }

    private HarnessEvent CreateReasoningPartUpdatedEvent(string messageId, int contentIndex, string text)
    {
        _contentPartReasoning[contentIndex] = text;
        return CreatePartUpdatedEvent(new ReasoningMessageEventPart
        {
            Id = ContentPartId(messageId, contentIndex),
            SessionId = _sessionId,
            MessageId = messageId,
            Text = text,
            Summary = null,
        });
    }

    private HarnessEvent CreatePendingToolPartUpdatedEvent(string messageId, int contentIndex, JsonElement? input)
    {
        var partId = ContentPartId(messageId, contentIndex);
        return CreatePartUpdatedEvent(new ToolMessageEventPart
        {
            Id = partId,
            SessionId = _sessionId,
            MessageId = messageId,
            ToolName = string.Empty,
            CallId = partId,
            State = new ToolPendingState { Input = input },
        });
    }

    private HarnessEvent CreateToolCallPartUpdatedEvent(string messageId, int contentIndex, PiToolCallContent toolCall)
    {
        var partId = ContentPartId(messageId, contentIndex);
        var callId = toolCall.Id ?? partId;
        var input = Clone(toolCall.Arguments) ?? TryParseJson(toolCall.PartialArgs);
        _toolsByCallId[callId] = new PiToolPartState(messageId, partId, callId, toolCall.Name ?? string.Empty, input, null, null);

        return CreatePartUpdatedEvent(new ToolMessageEventPart
        {
            Id = partId,
            SessionId = _sessionId,
            MessageId = messageId,
            ToolName = toolCall.Name ?? string.Empty,
            CallId = callId,
            State = new ToolPendingState { Input = input },
        });
    }

    private HarnessEvent CreateToolPartUpdatedEvent(PiToolPartState state, ToolInvocationState invocationState)
    {
        return CreatePartUpdatedEvent(new ToolMessageEventPart
        {
            Id = state.PartId,
            SessionId = _sessionId,
            MessageId = state.MessageId,
            ToolName = state.ToolName,
            CallId = state.ToolCallId,
            State = invocationState,
        });
    }

    private HarnessEvent CreatePartUpdatedEvent(MessageEventPart part)
    {
        var payload = new MessagePartUpdatedPayload { SessionId = _sessionId, Part = part };
        return CreateEvent(EventTypes.MessagePartUpdated, JsonSerializer.SerializeToElement(payload, InfrastructureJsonContext.Default.MessagePartUpdatedPayload));
    }

    private HarnessEvent CreateTextPartDeltaEvent(string messageId, string partId, string delta)
    {
        var payload = new MessagePartDeltaStreamedPayload
        {
            SessionId = _sessionId,
            MessageId = messageId,
            PartId = partId,
            Field = TextField,
            Delta = delta,
        };

        return CreateEvent(EventTypes.MessagePartDelta, JsonSerializer.SerializeToElement(payload, InfrastructureJsonContext.Default.MessagePartDeltaStreamedPayload));
    }

    private HarnessEvent CreateStepFinishEvent(PiMessage message)
    {
        var messageId = ResolveMessageId(message);
        return CreatePartUpdatedEvent(new StepFinishedMessageEventPart
        {
            Id = $"{messageId}-step-finish-{_turnIndex}",
            SessionId = _sessionId,
            MessageId = messageId,
            Index = _turnIndex++,
            Reason = message.StopReason,
            Cost = message.Usage?.Cost?.Total ?? 0,
            Tokens = message.Usage is null
                ? null
                : new MessageTokenUsage
                {
                    Input = message.Usage.Input,
                    Output = message.Usage.Output,
                    Reasoning = 0,
                },
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    private HarnessEvent CreateStatusEvent(string statusType)
        => CreateStatusEvent(statusType, null);

    private HarnessEvent CreateStatusEvent(string statusType, PiMessage? message)
    {
        var payload = new PiSessionStatusPayload
        {
            Status = new PiSessionStatus
            {
                Type = statusType,
                MessageId = message is null ? _lastAssistantMessageId ?? _activeAssistantMessageId : ResolveMessageId(message),
                Index = Math.Max(0, _turnIndex - 1),
                Agent = _agent,
                ModelId = message?.ResponseModel ?? message?.Model,
                Reason = message?.StopReason,
                Cost = message?.Usage?.Cost?.Total,
                CompletedAt = statusType == "idle" ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null,
                Tokens = message?.Usage is null
                    ? null
                    : new PiSessionStatusTokens
                    {
                        Input = message.Usage.Input,
                        Output = message.Usage.Output,
                        Reasoning = 0,
                    },
            },
        };

        return CreateEvent(EventTypes.SessionStatus, JsonSerializer.SerializeToElement(payload, PiMapperJsonContext.Default.PiSessionStatusPayload));
    }

    private HarnessEvent CreateInformationalStatus(string statusType, string activity, string? message)
    {
        var payload = new PiSessionStatusPayload
        {
            Status = new PiSessionStatus
            {
                Type = statusType,
                Activity = activity,
                Detail = message,
                MessageId = _lastAssistantMessageId ?? _activeAssistantMessageId,
                Index = Math.Max(0, _turnIndex - 1),
            },
        };

        return CreateEvent(EventTypes.SessionStatus, JsonSerializer.SerializeToElement(payload, PiMapperJsonContext.Default.PiSessionStatusPayload));
    }

    private HarnessEvent CreateCompactedEvent(PiCompactionEndEvent evt)
    {
        var payload = new PiCompactedPayload { Message = evt.Message, Success = evt.Success };
        return CreateEvent(EventTypes.SessionCompacted, JsonSerializer.SerializeToElement(payload, PiMapperJsonContext.Default.PiCompactedPayload));
    }

    private HarnessEvent CreateErrorEvent(string message)
    {
        var payload = new PiMapperErrorPayload { Message = message };
        return CreateEvent(EventTypes.Error, JsonSerializer.SerializeToElement(payload, PiMapperJsonContext.Default.PiMapperErrorPayload));
    }

    private HarnessEvent CreateSessionErrorEvent(string message)
    {
        var payload = new PiMapperErrorPayload { Message = message };
        return CreateEvent(EventTypes.SessionError, JsonSerializer.SerializeToElement(payload, PiMapperJsonContext.Default.PiMapperErrorPayload));
    }

    private HarnessEvent CreateSessionUpdatedEvent(PiSessionSwitchedEvent evt)
    {
        var payload = new PiSessionUpdatedPayload { SessionFile = evt.SessionFile, SessionId = evt.SessionId };
        return CreateEvent(EventTypes.SessionUpdated, JsonSerializer.SerializeToElement(payload, PiMapperJsonContext.Default.PiSessionUpdatedPayload));
    }

    private HarnessEvent CreateSessionUpdatedEvent(PiStateUpdateEvent evt)
    {
        var payload = new PiSessionUpdatedPayload
        {
            SessionFile = evt.State?.SessionFile,
            SessionId = evt.State?.SessionId,
            IsStreaming = evt.State?.IsStreaming,
            IsCompacting = evt.State?.IsCompacting,
            PendingMessageCount = evt.State?.PendingMessageCount,
        };
        return CreateEvent(EventTypes.SessionUpdated, JsonSerializer.SerializeToElement(payload, PiMapperJsonContext.Default.PiSessionUpdatedPayload));
    }

    private HarnessEvent CreateRawPayloadEvent(string type, JsonElement payload)
        => CreateEvent(type, payload);

    private HarnessEvent CreateEvent(string type, JsonElement? payload)
    {
        return new HarnessEvent
        {
            Type = type,
            SessionId = _sessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        };
    }

    private List<MessageEventPart> MapContentBlocks(PiMessage message, string messageId)
    {
        var parts = new List<MessageEventPart>(message.Content.Count);
        for (int i = 0; i < message.Content.Count; i++)
        {
            switch (message.Content[i])
            {
                case PiTextContent text:
                    parts.Add(new TextMessageEventPart
                    {
                        Id = ContentPartId(messageId, i),
                        SessionId = _sessionId,
                        MessageId = messageId,
                        Text = text.Text ?? string.Empty,
                    });
                    break;
                case PiThinkingContent thinking:
                    parts.Add(new ReasoningMessageEventPart
                    {
                        Id = ContentPartId(messageId, i),
                        SessionId = _sessionId,
                        MessageId = messageId,
                        Text = thinking.Thinking ?? string.Empty,
                        Summary = null,
                    });
                    break;
                case PiToolCallContent toolCall:
                    var partId = ContentPartId(messageId, i);
                    var callId = toolCall.Id ?? partId;
                    var input = Clone(toolCall.Arguments) ?? TryParseJson(toolCall.PartialArgs);
                    parts.Add(new ToolMessageEventPart
                    {
                        Id = partId,
                        SessionId = _sessionId,
                        MessageId = messageId,
                        ToolName = toolCall.Name ?? string.Empty,
                        CallId = callId,
                        State = new ToolPendingState { Input = input },
                    });
                    break;
            }
        }

        if (message.Role.Equals("toolResult", StringComparison.OrdinalIgnoreCase) && parts.Count == 0)
        {
            parts.Add(new TextMessageEventPart
            {
                Id = ContentPartId(messageId, 0),
                SessionId = _sessionId,
                MessageId = messageId,
                Text = message.ErrorMessage ?? string.Empty,
            });
        }

        return parts;
    }

    private PiToolPartState UpsertToolState(string toolCallId, string toolName, JsonElement? args)
    {
        if (_toolsByCallId.TryGetValue(toolCallId, out var existing))
        {
            return existing with
            {
                ToolName = string.IsNullOrWhiteSpace(existing.ToolName) ? toolName : existing.ToolName,
                Input = existing.Input ?? Clone(args),
            };
        }

        var messageId = ResolveToolHostMessageId(toolCallId);
        return new PiToolPartState(messageId, ToolPartId(messageId, toolCallId), toolCallId, toolName, Clone(args), null, null);
    }

    private string ResolveToolHostMessageId(string toolCallId)
    {
        if (_toolsByCallId.TryGetValue(toolCallId, out var existing))
            return existing.MessageId;

        return _lastAssistantMessageId ?? _activeAssistantMessageId ?? _activeMessageId ?? NextSyntheticMessageId(AssistantRole);
    }

    private string ResolveActiveAssistantMessageId()
        => _activeAssistantMessageId ?? _lastAssistantMessageId ?? NextSyntheticMessageId(AssistantRole);

    private string ResolveMessageId(PiMessage message)
    {
        var timestamp = message.Timestamp?.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(timestamp))
        {
            var role = Sanitize(message.Role);
            var suffix = message.Role.Equals("toolResult", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(message.ToolCallId)
                ? $"-{Sanitize(message.ToolCallId)}"
                : string.Empty;
            return $"pi-{_sessionId}-{role}-{timestamp}{suffix}";
        }

        if (IsAssistant(message) && _activeAssistantMessageId is not null)
            return _activeAssistantMessageId;

        return NextSyntheticMessageId(ToFleetRole(message.Role));
    }

    private string NextSyntheticMessageId(string role)
    {
        _messageSequence++;
        return $"pi-{_sessionId}-{Sanitize(role)}-{_messageSequence.ToString(CultureInfo.InvariantCulture)}";
    }

    private string CurrentText(int contentIndex)
        => _contentPartText.TryGetValue(contentIndex, out var text) ? text : string.Empty;

    private string CurrentReasoning(int contentIndex)
        => _contentPartReasoning.TryGetValue(contentIndex, out var text) ? text : string.Empty;

    private string FinalText(int contentIndex, string? content, PiMessage? message)
        => content ?? TextAtContentIndex(message, contentIndex) ?? CurrentText(contentIndex);

    private string FinalReasoning(int contentIndex, string? content, PiMessage? message)
        => content ?? ReasoningAtContentIndex(message, contentIndex) ?? CurrentReasoning(contentIndex);

    private static string? TextAtContentIndex(PiMessage? message, int contentIndex)
    {
        return message is not null
            && contentIndex >= 0
            && contentIndex < message.Content.Count
            && message.Content[contentIndex] is PiTextContent text
            ? text.Text
            : null;
    }

    private static string? ReasoningAtContentIndex(PiMessage? message, int contentIndex)
    {
        return message is not null
            && contentIndex >= 0
            && contentIndex < message.Content.Count
            && message.Content[contentIndex] is PiThinkingContent thinking
            ? thinking.Thinking
            : null;
    }

    private static JsonElement? ToolResultToJson(PiToolResult? result)
    {
        if (result is null)
            return null;

        if (result.Details is { ValueKind: not JsonValueKind.Undefined } details)
            return details.Clone();

        var text = string.Concat(result.Content.OfType<PiTextContent>().Select(content => content.Text));
        return text.Length == 0 ? null : JsonSerializer.SerializeToElement(text, PiMapperJsonContext.Default.String);
    }

    private static JsonElement? ToolResultDetails(PiToolResult? result)
        => result?.Details is { ValueKind: not JsonValueKind.Undefined } details ? details.Clone() : null;

    private static JsonElement? TryParseJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(raw, PiMapperJsonContext.Default.String);
        }
    }

    private static JsonElement? Clone(JsonElement? element)
        => element is { ValueKind: not JsonValueKind.Undefined } value ? value.Clone() : null;

    private static string ContentPartId(string messageId, int contentIndex)
        => $"{messageId}-content-{contentIndex.ToString(CultureInfo.InvariantCulture)}";

    private static string ToolPartId(string messageId, string toolCallId)
        => $"{messageId}-tool-{Sanitize(toolCallId)}";

    private static bool IsAssistant(PiMessage message)
        => message.Role.Equals(AssistantRole, StringComparison.OrdinalIgnoreCase);

    private static bool IsAborted(PiMessage message)
        => message.StopReason?.Equals("aborted", StringComparison.OrdinalIgnoreCase) == true;

    private static bool ShouldSuppressMessage(PiMessage message)
        => string.IsNullOrWhiteSpace(message.Role);

    private static string ToFleetRole(string role)
        => role.Equals(UserRole, StringComparison.OrdinalIgnoreCase) ? UserRole : AssistantRole;

    private string? AgentForMessage(PiMessage message)
    {
        if (message.Role.Equals("toolResult", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(message.ToolName) ? "tool" : $"tool:{message.ToolName}";

        return _agent;
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(static ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        return new string(chars);
    }

    private sealed record PiToolPartState(
        string MessageId,
        string PartId,
        string ToolCallId,
        string ToolName,
        JsonElement? Input,
        JsonElement? Output,
        bool? IsError);
}

internal sealed record PiSessionStatusPayload
{
    [JsonPropertyName("status")] public required PiSessionStatus Status { get; init; }
}

internal sealed record PiSessionStatus
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("activity")] public string? Activity { get; init; }
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("messageID")] public string? MessageId { get; init; }
    [JsonPropertyName("index")] public int? Index { get; init; }
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("modelID")] public string? ModelId { get; init; }
    [JsonPropertyName("parentID")] public string? ParentId { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("cost")] public double? Cost { get; init; }
    [JsonPropertyName("completedAt")] public long? CompletedAt { get; init; }
    [JsonPropertyName("tokens")] public PiSessionStatusTokens? Tokens { get; init; }
}

internal sealed record PiSessionStatusTokens
{
    [JsonPropertyName("input")] public double Input { get; init; }
    [JsonPropertyName("output")] public double Output { get; init; }
    [JsonPropertyName("reasoning")] public double Reasoning { get; init; }
}

internal sealed record PiMapperErrorPayload
{
    [JsonPropertyName("message")] public required string Message { get; init; }
}

internal sealed record PiSessionUpdatedPayload
{
    [JsonPropertyName("sessionFile")] public string? SessionFile { get; init; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; init; }
    [JsonPropertyName("isStreaming")] public bool? IsStreaming { get; init; }
    [JsonPropertyName("isCompacting")] public bool? IsCompacting { get; init; }
    [JsonPropertyName("pendingMessageCount")] public int? PendingMessageCount { get; init; }
}

internal sealed record PiCompactedPayload
{
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("success")] public bool? Success { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PiSessionStatusPayload))]
[JsonSerializable(typeof(PiMapperErrorPayload))]
[JsonSerializable(typeof(PiSessionUpdatedPayload))]
[JsonSerializable(typeof(PiCompactedPayload))]
[JsonSerializable(typeof(string))]
internal sealed partial class PiMapperJsonContext : JsonSerializerContext
{
}
