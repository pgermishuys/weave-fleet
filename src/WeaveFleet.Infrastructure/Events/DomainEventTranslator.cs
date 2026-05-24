#pragma warning disable CA1848, CA1873 // Temporary diagnostic logging
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Events;

/// <summary>
/// Translates raw <see cref="HarnessEvent"/> instances into strongly typed <see cref="DomainEvent"/> instances.
/// </summary>
internal sealed class DomainEventTranslator
{
    private const string IdleStatus = "idle";
    private const string BusyStatus = "busy";
    private const string WorkingStatus = "working";
    private const string DelegationCreatedEventType = "delegation.created";
    private const string DelegationUpdatedEventType = "delegation.updated";
    private const string DelegationCompletedEventType = "delegation.completed";

    private static readonly Action<ILogger, string, Exception?> LogUnknownEventType =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "UnknownDomainEventType"),
            "Dropping unknown harness event type {EventType} during domain-event translation.");

    private readonly ILogger<DomainEventTranslator> _logger;
    private string _activityStatus = IdleStatus;
    private int _nextTurnIndex;
    private TurnContext? _activeTurn;
    private AssistantTurnSnapshot? _lastAssistantSnapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainEventTranslator"/> class.
    /// </summary>
    public DomainEventTranslator(ILogger<DomainEventTranslator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Translates a single raw harness event into a strongly typed domain event.
    /// </summary>
    /// <param name="evt">The raw harness event to translate.</param>
    /// <returns>
    /// A typed <see cref="DomainEvent"/> when the event is recognized and mapped; otherwise, <see langword="null"/>.
    /// </returns>
    public DomainEvent? Translate(HarnessEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _logger.LogDebug("[Translator] Translating type={Type} session={Session}", evt.Type, evt.SessionId);

        var result = evt.Type switch
        {
            EventTypes.MessageCreated => TranslateMessageCreated(evt),
            EventTypes.MessageUpdated => TranslateMessageUpdated(evt),
            EventTypes.MessagePartUpdated => TranslateMessagePartUpdated(evt),
            EventTypes.MessagePartDelta => TranslateMessagePartDeltaStreamed(evt),
            EventTypes.SessionCreated => TranslateSessionStarted(evt),
            EventTypes.SessionDeleted => TranslateSessionDeleted(evt),
            EventTypes.SessionStatus => TranslateSessionStatus(evt),
            EventTypes.SessionIdle => TranslateSessionIdle(evt),
            DelegationCreatedEventType => TranslateDelegationCreated(evt),
            DelegationUpdatedEventType => TranslateDelegationUpdated(evt),
            DelegationCompletedEventType => TranslateDelegationCompleted(evt),

            // message.removed and message.part.removed are durable persistence signals only.
            EventTypes.MessageRemoved or EventTypes.MessagePartRemoved => null,

            // session.updated/session.error/session.compacted/session.diff do not yet have domain-event counterparts.
            EventTypes.SessionUpdated or EventTypes.SessionError or EventTypes.SessionCompacted or EventTypes.SessionDiff => null,

            // error/server.* are transport/control signals and are intentionally not surfaced as domain events.
            EventTypes.Error or EventTypes.ServerHeartbeat or EventTypes.ServerConnected => null,

            // permission.* events are UI interaction signals rather than domain events.
            _ when EventTypes.IsPermissionEvent(evt.Type) => null,

            _ => DropUnknown(evt.Type),
        };

        _logger.LogDebug("[Translator] Result type={Type} => {Result}", evt.Type, result?.GetType().Name ?? "null");
        return result;
    }

    private MessageCreated? TranslateMessageCreated(HarnessEvent evt)
    {
        var payload = DeserializePayload(evt, InfrastructureJsonContext.Default.MessageLifecyclePayload);
        if (payload is null)
            return null;

        var normalized = NormalizeMessageLifecyclePayload(evt, payload);
        TrackAssistantMessage(normalized.Info);
        return new MessageCreated { Payload = normalized };
    }

    private MessageUpdated? TranslateMessageUpdated(HarnessEvent evt)
    {
        var payload = DeserializePayload(evt, InfrastructureJsonContext.Default.MessageLifecyclePayload);
        if (payload is null)
            return null;

        var normalized = NormalizeMessageLifecyclePayload(evt, payload);
        TrackAssistantMessage(normalized.Info);
        return new MessageUpdated { Payload = normalized };
    }

    private MessagePartUpdated? TranslateMessagePartUpdated(HarnessEvent evt)
    {
        var payload = DeserializePayload(evt, InfrastructureJsonContext.Default.MessagePartUpdatedPayload);
        if (payload is null)
            return null;

        var normalized = NormalizeMessagePartUpdatedPayload(evt, payload);
        TrackMessagePart(normalized.Part);
        return new MessagePartUpdated { Payload = normalized };
    }

    private static MessagePartDeltaStreamed? TranslateMessagePartDeltaStreamed(HarnessEvent evt)
    {
        var payload = DeserializePayload(evt, InfrastructureJsonContext.Default.MessagePartDeltaStreamedPayload);
        if (payload is null)
            return null;

        return new MessagePartDeltaStreamed
        {
            Payload = payload with { SessionId = ResolveSessionId(evt) }
        };
    }

    private static SessionStarted? TranslateSessionStarted(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return null;

        var sessionId = ResolveSessionId(evt);
        var infoElement = TryGetObjectProperty(payload, "info") ?? payload;

        return new SessionStarted
        {
            Payload = new SessionStartedPayload
            {
                SessionId = sessionId,
                InstanceId = GetStringProperty(payload, "instanceId", "instanceID")
                    ?? GetStringProperty(infoElement, "instanceId", "instanceID"),
                WorkspaceId = GetStringProperty(payload, "workspaceId", "workspaceID")
                    ?? GetStringProperty(infoElement, "workspaceId", "workspaceID")
                    ?? GetStringProperty(infoElement, "directory"),
                Title = GetStringProperty(payload, "title")
                    ?? GetStringProperty(infoElement, "title"),
                ProjectId = GetStringProperty(payload, "projectId", "projectID")
                    ?? GetStringProperty(infoElement, "projectId", "projectID"),
                ParentSessionId = GetStringProperty(payload, "parentSessionId", "parentID")
                    ?? GetStringProperty(infoElement, "parentSessionId", "parentID"),
                IsHidden = GetBooleanProperty(payload, "isHidden")
                    ?? GetBooleanProperty(infoElement, "isHidden"),
            }
        };
    }

    private static SessionDeleted TranslateSessionDeleted(HarnessEvent evt)
    {
        var payload = DeserializePayload(evt, InfrastructureJsonContext.Default.SessionDeletedPayload);

        return new SessionDeleted
        {
            Payload = new SessionDeletedPayload
            {
                SessionId = payload?.SessionId ?? ResolveSessionId(evt)
            }
        };
    }

    private DomainEvent? TranslateSessionStatus(HarnessEvent evt)
    {
        var signal = ReadTurnSignal(evt);
        if (signal.Status is null)
            return null;

        if (string.Equals(signal.Status, IdleStatus, StringComparison.OrdinalIgnoreCase))
        {
            _activityStatus = IdleStatus;
            return new TurnEnded { Payload = CreateTurnEndedPayload(evt, signal) };
        }

        if (string.Equals(signal.Status, BusyStatus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(signal.Status, WorkingStatus, StringComparison.OrdinalIgnoreCase))
        {
            var wasIdle = string.Equals(_activityStatus, IdleStatus, StringComparison.OrdinalIgnoreCase);
            _activityStatus = BusyStatus;

            if (!wasIdle)
            {
                UpdateActiveTurn(signal, ResolveSessionId(evt));
                return null;
            }

            var payload = CreateTurnStartedPayload(evt, signal);
            _activeTurn = new TurnContext(
                payload.SessionId,
                payload.MessageId,
                payload.Index,
                payload.Agent,
                payload.ModelId,
                payload.ParentId);

            return new TurnStarted { Payload = payload };
        }

        return null;
    }

    private TurnEnded TranslateSessionIdle(HarnessEvent evt)
    {
        _activityStatus = IdleStatus;
        return new TurnEnded { Payload = CreateTurnEndedPayload(evt, ReadTurnSignal(evt)) };
    }

    private static DelegationCreated? TranslateDelegationCreated(HarnessEvent evt)
    {
        var payload = DeserializePayload(evt, InfrastructureJsonContext.Default.DelegationCreatedPayload);
        if (payload is null)
            return null;

        return new DelegationCreated
        {
            Payload = payload with
            {
                ParentSessionId = string.IsNullOrWhiteSpace(payload.ParentSessionId)
                    ? ResolveSessionId(evt)
                    : payload.ParentSessionId,
            }
        };
    }

    private static DelegationUpdated? TranslateDelegationUpdated(HarnessEvent evt)
    {
        var payload = DeserializePayload(evt, InfrastructureJsonContext.Default.DelegationUpdatedPayload);
        if (payload is null)
            return null;

        return new DelegationUpdated
        {
            Payload = payload with
            {
                ParentSessionId = string.IsNullOrWhiteSpace(payload.ParentSessionId)
                    ? ResolveSessionId(evt)
                    : payload.ParentSessionId,
            }
        };
    }

    private static DelegationCompleted? TranslateDelegationCompleted(HarnessEvent evt)
    {
        var payload = DeserializePayload(evt, InfrastructureJsonContext.Default.DelegationCompletedPayload);
        if (payload is null)
            return null;

        return new DelegationCompleted
        {
            Payload = payload with
            {
                ParentSessionId = string.IsNullOrWhiteSpace(payload.ParentSessionId)
                    ? ResolveSessionId(evt)
                    : payload.ParentSessionId,
            }
        };
    }

    private DomainEvent? DropUnknown(string eventType)
    {
        LogUnknownEventType(_logger, eventType, null);
        return null;
    }

    private void TrackAssistantMessage(MessageEventInfo info)
    {
        if (!string.Equals(info.Role, "assistant", StringComparison.Ordinal))
            return;

        var index = _activeTurn?.Index ?? (_nextTurnIndex > 0 ? _nextTurnIndex - 1 : 0);
        _lastAssistantSnapshot = new AssistantTurnSnapshot(
            new TurnContext(info.SessionId, info.Id, index, info.Agent, info.ModelId, info.ParentId),
            info.Cost,
            info.Tokens,
            info.Time.Completed);

        if (_activeTurn is not null)
        {
            _activeTurn = new TurnContext(info.SessionId, info.Id, _activeTurn.Index, info.Agent, info.ModelId, info.ParentId);
        }
    }

    private void TrackMessagePart(MessageEventPart part)
    {
        if (_activeTurn is null)
        {
            if (part is StepStartedMessageEventPart stepStarted)
            {
                _activeTurn = new TurnContext(part.SessionId, part.MessageId, stepStarted.Index, null, null, null);
                _nextTurnIndex = Math.Max(_nextTurnIndex, stepStarted.Index + 1);
            }

            if (part is StepFinishedMessageEventPart stepFinished)
            {
                _lastAssistantSnapshot = new AssistantTurnSnapshot(
                    new TurnContext(part.SessionId, part.MessageId, stepFinished.Index, null, null, null),
                    stepFinished.Cost,
                    stepFinished.Tokens,
                    stepFinished.CompletedAt);
                _nextTurnIndex = Math.Max(_nextTurnIndex, stepFinished.Index + 1);
            }

            return;
        }

        if (part is StepStartedMessageEventPart activeStepStarted)
        {
            _activeTurn = _activeTurn with { Index = activeStepStarted.Index, MessageId = part.MessageId };
            _nextTurnIndex = Math.Max(_nextTurnIndex, activeStepStarted.Index + 1);
            return;
        }

        if (part is StepFinishedMessageEventPart activeStepFinished)
        {
            _activeTurn = _activeTurn with { Index = activeStepFinished.Index, MessageId = part.MessageId };
            _lastAssistantSnapshot = new AssistantTurnSnapshot(
                _activeTurn,
                activeStepFinished.Cost,
                activeStepFinished.Tokens,
                activeStepFinished.CompletedAt,
                activeStepFinished.Reason);
            _nextTurnIndex = Math.Max(_nextTurnIndex, activeStepFinished.Index + 1);
        }
    }

    private void UpdateActiveTurn(TurnSignal signal, string sessionId)
    {
        if (_activeTurn is null)
        {
            return;
        }

        _activeTurn = new TurnContext(
            sessionId,
            signal.MessageId ?? _activeTurn.MessageId,
            signal.Index ?? _activeTurn.Index,
            signal.Agent ?? _activeTurn.Agent,
            signal.ModelId ?? _activeTurn.ModelId,
            signal.ParentId ?? _activeTurn.ParentId);
    }

    private TurnStartedPayload CreateTurnStartedPayload(HarnessEvent evt, TurnSignal signal)
    {
        var sessionId = ResolveSessionId(evt);
        var index = signal.Index ?? _activeTurn?.Index ?? _nextTurnIndex;
        _nextTurnIndex = Math.Max(_nextTurnIndex, index + 1);

        var lastContext = _lastAssistantSnapshot?.Context;
        return new TurnStartedPayload
        {
            SessionId = sessionId,
            MessageId = signal.MessageId ?? _activeTurn?.MessageId ?? lastContext?.MessageId ?? string.Empty,
            Index = index,
            Agent = signal.Agent ?? _activeTurn?.Agent ?? lastContext?.Agent,
            ModelId = signal.ModelId ?? _activeTurn?.ModelId ?? lastContext?.ModelId,
            ParentId = signal.ParentId ?? _activeTurn?.ParentId ?? lastContext?.ParentId,
        };
    }

    private TurnEndedPayload CreateTurnEndedPayload(HarnessEvent evt, TurnSignal signal)
    {
        var sessionId = ResolveSessionId(evt);
        var fallbackContext = _activeTurn
            ?? _lastAssistantSnapshot?.Context
            ?? new TurnContext(sessionId, string.Empty, Math.Max(0, _nextTurnIndex - 1), null, null, null);

        var completedSnapshot = _lastAssistantSnapshot;
        var payload = new TurnEndedPayload
        {
            SessionId = sessionId,
            MessageId = signal.MessageId ?? fallbackContext.MessageId,
            Index = signal.Index ?? fallbackContext.Index,
            Reason = signal.Reason ?? completedSnapshot?.Reason,
            Cost = signal.Cost ?? completedSnapshot?.Cost ?? 0,
            Tokens = signal.Tokens is not null
                ? new TurnTokenUsage
                {
                    Input = signal.Tokens.Input,
                    Output = signal.Tokens.Output,
                    Reasoning = signal.Tokens.Reasoning,
                }
                : completedSnapshot?.Tokens is not null
                    ? new TurnTokenUsage
                    {
                        Input = completedSnapshot.Tokens.Input,
                        Output = completedSnapshot.Tokens.Output,
                        Reasoning = completedSnapshot.Tokens.Reasoning,
                    }
                    : null,
            CompletedAt = signal.CompletedAt ?? completedSnapshot?.CompletedAt,
        };

        _activeTurn = null;
        return payload;
    }

    private static T? DeserializePayload<T>(HarnessEvent evt, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (evt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return null;

        try
        {
            return payload.Deserialize(typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static MessageLifecyclePayload NormalizeMessageLifecyclePayload(HarnessEvent evt, MessageLifecyclePayload payload)
    {
        var sessionId = ResolveSessionId(evt);
        var parts = payload.Parts ?? [];
        return payload with
        {
            Info = payload.Info with { SessionId = sessionId },
            Parts = parts.Select(part => NormalizeMessageEventPart(part, sessionId)).ToArray(),
        };
    }

    private static MessagePartUpdatedPayload NormalizeMessagePartUpdatedPayload(HarnessEvent evt, MessagePartUpdatedPayload payload)
    {
        var sessionId = ResolveSessionId(evt);
        return payload with
        {
            SessionId = sessionId,
            Part = NormalizeMessageEventPart(payload.Part, sessionId),
        };
    }

    private static MessageEventPart NormalizeMessageEventPart(MessageEventPart part, string sessionId)
    {
        return part switch
        {
            TextMessageEventPart text => text with { SessionId = sessionId },
            ReasoningMessageEventPart reasoning => reasoning with { SessionId = sessionId },
            ToolMessageEventPart tool => tool with { SessionId = sessionId },
            FileMessageEventPart file => file with { SessionId = sessionId },
            StepStartedMessageEventPart stepStarted => stepStarted with { SessionId = sessionId },
            StepFinishedMessageEventPart stepFinished => stepFinished with { SessionId = sessionId },
            _ => part,
        };
    }

    private static TurnSignal ReadTurnSignal(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return new TurnSignal(null, null, null, null, null, null, null, null, null);

        var statusElement = TryGetObjectProperty(payload, "status") ?? payload;
        var status = GetStringProperty(payload, "activityStatus")
            ?? GetStringProperty(statusElement, "type")
            ?? GetStringProperty(payload, "status");

        var tokensElement = TryGetObjectProperty(statusElement, "tokens")
            ?? TryGetObjectProperty(payload, "tokens");

        return new TurnSignal(
            status,
            GetStringProperty(statusElement, "messageID", "messageId")
                ?? GetStringProperty(payload, "messageID", "messageId"),
            GetInt32Property(statusElement, "index")
                ?? GetInt32Property(payload, "index"),
            GetStringProperty(statusElement, "agent")
                ?? GetStringProperty(payload, "agent"),
            GetStringProperty(statusElement, "modelID", "modelId")
                ?? GetStringProperty(payload, "modelID", "modelId"),
            GetStringProperty(statusElement, "parentID", "parentId")
                ?? GetStringProperty(payload, "parentID", "parentId"),
            GetStringProperty(statusElement, "reason")
                ?? GetStringProperty(payload, "reason"),
            GetDoubleProperty(statusElement, "cost")
                ?? GetDoubleProperty(payload, "cost"),
            GetInt64Property(statusElement, "completedAt")
                ?? GetInt64Property(payload, "completedAt"),
            tokensElement is null
                ? null
                : new TurnTokens(
                    GetDoubleProperty(tokensElement.Value, "input") ?? 0,
                    GetDoubleProperty(tokensElement.Value, "output") ?? 0,
                    GetDoubleProperty(tokensElement.Value, "reasoning") ?? 0));
    }

    private static JsonElement? TryGetObjectProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            return null;

        return property;
    }

    private static string? GetStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
                continue;

            return property.GetString();
        }

        return null;
    }

    private static bool? GetBooleanProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return property.GetBoolean();
        }

        return null;
    }

    private static int? GetInt32Property(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
                return value;
        }

        return null;
    }

    private static long? GetInt64Property(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
                return value;
        }

        return null;
    }

    private static double? GetDoubleProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
                return value;
        }

        return null;
    }

    private static string ResolveSessionId(HarnessEvent evt)
        => evt.FleetSessionId ?? evt.SessionId;

    private sealed record TurnContext(
        string SessionId,
        string MessageId,
        int Index,
        string? Agent,
        string? ModelId,
        string? ParentId);

    private sealed record AssistantTurnSnapshot(
        TurnContext Context,
        double? Cost,
        MessageTokenUsage? Tokens,
        long? CompletedAt,
        string? Reason = null);

    private sealed record TurnSignal(
        string? Status,
        string? MessageId,
        int? Index,
        string? Agent,
        string? ModelId,
        string? ParentId,
        string? Reason,
        double? Cost,
        long? CompletedAt,
        TurnTokens? Tokens = null);

    private sealed record TurnTokens(double Input, double Output, double Reasoning);
}
