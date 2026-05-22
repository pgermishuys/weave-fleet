using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// Implements the snapshot-first WebSocket v2 protocol.
/// </summary>
internal static class WebSocketV2Protocol
{
    private const string SubscribeV2MessageType = "subscribe_v2";
    private const string LoadHistoryMessageType = "load_history";
    private const string SnapshotMessageType = "snapshot";
    private const string EventV2MessageType = "event_v2";
    private const string HistoryMessageType = "history";
    private const string SessionTopicPrefix = "session:";
    private const string IdleStatus = "idle";
    private const string BusyStatus = "busy";
    private const string WorkingStatus = "working";
    private const string DelegationCreatedEventType = "delegation.created";
    private const string DelegationUpdatedEventType = "delegation.updated";
    private const string DelegationCompletedEventType = "delegation.completed";

    /// <summary>
    /// Gets the WebSocket v2 subscribe message type.
    /// </summary>
    public static string SubscribeMessageType => SubscribeV2MessageType;

    /// <summary>
    /// Gets the WebSocket v2 history request message type.
    /// </summary>
    public static string LoadHistoryRequestType => LoadHistoryMessageType;

    /// <summary>
    /// Parses the requested topics from a client message.
    /// </summary>
    public static IReadOnlyList<string> ParseTopics(JsonElement root)
    {
        if (!root.TryGetProperty("topics", out var topicsProp) || topicsProp.ValueKind != JsonValueKind.Array)
            return [];

        return topicsProp
            .EnumerateArray()
            .Select(topic => topic.GetString())
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Select(topic => topic!)
            .ToList();
    }

    /// <summary>
    /// Attempts to parse a v2 session topic.
    /// </summary>
    public static bool TryParseSessionTopic(string topic, out string sessionId)
    {
        if (topic.StartsWith(SessionTopicPrefix, StringComparison.Ordinal)
            && topic.Length > SessionTopicPrefix.Length)
        {
            sessionId = topic[SessionTopicPrefix.Length..];
            return true;
        }

        sessionId = string.Empty;
        return false;
    }

    /// <summary>
    /// Sends a snapshot envelope for a v2 subscription.
    /// </summary>
    public static Task SendSnapshotAsync(
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        string topic,
        SessionSnapshot snapshot,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            new WsSnapshotPayload(SnapshotMessageType, topic, snapshot),
            ApiJsonContext.Default.WsSnapshotPayload);
        return SendTextAsync(webSocket, sendLock, json, ct);
    }

    /// <summary>
    /// Sends a paginated history envelope for a v2 subscription.
    /// </summary>
    public static Task SendHistoryAsync(
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        string topic,
        SessionSnapshot snapshot,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            new WsHistoryPayload(
                HistoryMessageType,
                topic,
                new WsHistoryPagePayload(snapshot.Messages, snapshot.Cursor, snapshot.HasMore)),
            ApiJsonContext.Default.WsHistoryPayload);
        return SendTextAsync(webSocket, sendLock, json, ct);
    }

    /// <summary>
    /// Sends a translated v2 domain event envelope.
    /// </summary>
    public static async Task SendEventAsync(
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        BroadcastEvent broadcastEvent,
        WebSocketV2SubscriptionState subscriptionState,
        CancellationToken ct)
    {
        var domainEvent = subscriptionState.Translate(broadcastEvent);
        if (domainEvent is null)
            return;

        var json = JsonSerializer.Serialize(
            new WsEventV2Payload(EventV2MessageType, broadcastEvent.Topic, broadcastEvent.EventId, domainEvent),
            ApiJsonContext.Default.WsEventV2Payload);

        await SendTextAsync(webSocket, sendLock, json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a UTF-8 text frame while serializing concurrent writers.
    /// </summary>
    public static async Task SendTextAsync(
        WebSocket webSocket,
        SemaphoreSlim sendLock,
        string payload,
        CancellationToken ct)
    {
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (webSocket.State != WebSocketState.Open)
                return;

            var bytes = Encoding.UTF8.GetBytes(payload);
            await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// Builds a snapshot for the specified session.
    /// </summary>
    public static async Task<SessionSnapshot> BuildSnapshotAsync(
        IServiceProvider services,
        string sessionId,
        string? cursor,
        CancellationToken ct)
    {
        var snapshotBuilder = services.GetService<ISessionSnapshotBuilder>();
        if (snapshotBuilder is not null)
        {
            return await snapshotBuilder.BuildAsync(sessionId, cursor: cursor).ConfigureAwait(false);
        }

        var fallbackBuilder = new PlaceholderSessionSnapshotBuilder(
            services.GetRequiredService<SessionService>(),
            services.GetRequiredService<DelegationService>(),
            services.GetRequiredService<SessionActivityTracker>());
        return await fallbackBuilder.BuildAsync(sessionId, cursor: cursor).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the most recent snapshot for the specified session.
    /// </summary>
    public static Task<SessionSnapshot> BuildSnapshotAsync(
        IServiceProvider services,
        string sessionId,
        CancellationToken ct)
        => BuildSnapshotAsync(services, sessionId, cursor: null, ct: ct);

    /// <summary>
    /// Attempts to parse a history request.
    /// </summary>
    public static bool TryParseHistoryRequest(JsonElement root, out string topic, out string? cursor)
    {
        topic = string.Empty;
        cursor = null;

        if (!root.TryGetProperty("topic", out var topicProp) || topicProp.ValueKind != JsonValueKind.String)
            return false;

        topic = topicProp.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(topic))
            return false;

        if (root.TryGetProperty("cursor", out var cursorProp) && cursorProp.ValueKind == JsonValueKind.String)
            cursor = cursorProp.GetString();

        return true;
    }

    private sealed class PlaceholderSessionSnapshotBuilder : ISessionSnapshotBuilder
    {
        private readonly SessionService _sessionService;
        private readonly DelegationService _delegationService;
        private readonly SessionActivityTracker _activityTracker;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlaceholderSessionSnapshotBuilder"/> class.
        /// </summary>
        public PlaceholderSessionSnapshotBuilder(
            SessionService sessionService,
            DelegationService delegationService,
            SessionActivityTracker activityTracker)
        {
            _sessionService = sessionService;
            _delegationService = delegationService;
            _activityTracker = activityTracker;
        }

        /// <inheritdoc />
        public async Task<SessionSnapshot> BuildAsync(string sessionId, int pageSize = 100, string? cursor = null)
        {
            _ = pageSize;
            _ = cursor;

            var sessionResult = await _sessionService.GetSessionAsync(sessionId).ConfigureAwait(false);
            if (sessionResult.IsFailure)
            {
                return new SessionSnapshot
                {
                    Session = new SessionSnapshotSession
                    {
                        Id = sessionId,
                        Title = sessionId,
                        Status = "unknown"
                    },
                    ActivityStatus = IdleStatus,
                    Messages = [],
                    Delegations = []
                };
            }

            var session = sessionResult.Value;
            var delegations = await _delegationService.GetDelegationsAsync(sessionId).ConfigureAwait(false);
            var activityStatus = _activityTracker.GetEffectiveActivityStatus(sessionId)
                ?? session.ActivityStatus
                ?? IdleStatus;

            return new SessionSnapshot
            {
                Session = new SessionSnapshotSession
                {
                    Id = session.Id,
                    Title = session.Title,
                    Status = session.Status
                },
                ActivityStatus = activityStatus,
                Messages = [],
                Delegations = delegations.Select(ToSnapshotDelegation).ToArray(),
                LastEventId = null,
                HasMore = false,
                Cursor = null
            };
        }

        private static SessionSnapshotDelegation ToSnapshotDelegation(DelegationDto delegation)
            => new()
            {
                DelegationId = delegation.DelegationId,
                ParentToolCallId = delegation.ParentToolCallId,
                ChildSessionId = delegation.ChildSessionId,
                Title = delegation.Title,
                Status = delegation.Status,
                CreatedAt = delegation.CreatedAt
            };
    }

    internal sealed class WebSocketV2DomainEventTranslator
    {
        private string _activityStatus = IdleStatus;
        private int _nextTurnIndex;
        private TurnContext? _activeTurn;
        private AssistantTurnSnapshot? _lastAssistantSnapshot;

        /// <summary>
        /// Seeds translator state from the delivered snapshot.
        /// </summary>
        public void Initialize(SessionSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            _activityStatus = string.IsNullOrWhiteSpace(snapshot.ActivityStatus)
                ? IdleStatus
                : snapshot.ActivityStatus;

            _nextTurnIndex = 0;
            _activeTurn = null;
            _lastAssistantSnapshot = null;

            foreach (var message in snapshot.Messages)
            {
                TrackAssistantMessage(message.Info);
                foreach (var part in message.Parts)
                {
                    TrackMessagePart(part);
                }
            }

            if (string.Equals(_activityStatus, BusyStatus, StringComparison.OrdinalIgnoreCase))
            {
                var context = _lastAssistantSnapshot?.Context
                    ?? new TurnContext(snapshot.Session.Id, string.Empty, Math.Max(0, _nextTurnIndex - 1), null, null, null);
                _activeTurn = context;
            }
        }

        /// <summary>
        /// Translates a raw broadcast event into a strongly typed domain event.
        /// </summary>
        public DomainEvent? Translate(BroadcastEvent broadcastEvent)
        {
            ArgumentNullException.ThrowIfNull(broadcastEvent);

            return broadcastEvent.Type switch
            {
                EventTypes.MessageCreated => TranslateMessageCreated(broadcastEvent),
                EventTypes.MessageUpdated => TranslateMessageUpdated(broadcastEvent),
                EventTypes.MessagePartUpdated => TranslateMessagePartUpdated(broadcastEvent),
                EventTypes.MessagePartDelta => TranslateMessagePartDeltaStreamed(broadcastEvent),
                EventTypes.SessionCreated => TranslateSessionStarted(broadcastEvent),
                EventTypes.SessionDeleted => TranslateSessionDeleted(broadcastEvent),
                EventTypes.SessionStatus => TranslateSessionStatus(broadcastEvent),
                EventTypes.SessionIdle => TranslateSessionIdle(broadcastEvent),
                DelegationCreatedEventType => TranslateDelegationCreated(broadcastEvent),
                DelegationUpdatedEventType => TranslateDelegationUpdated(broadcastEvent),
                DelegationCompletedEventType => TranslateDelegationCompleted(broadcastEvent),
                _ => null,
            };
        }

        private MessageCreated? TranslateMessageCreated(BroadcastEvent broadcastEvent)
        {
            var payload = DeserializePayload(broadcastEvent.Payload, ApiJsonContext.Default.MessageLifecyclePayload);
            if (payload is null)
                return null;

            var normalized = NormalizeMessageLifecyclePayload(broadcastEvent, payload);
            TrackAssistantMessage(normalized.Info);
            return new MessageCreated { Payload = normalized };
        }

        private MessageUpdated? TranslateMessageUpdated(BroadcastEvent broadcastEvent)
        {
            var payload = DeserializePayload(broadcastEvent.Payload, ApiJsonContext.Default.MessageLifecyclePayload);
            if (payload is null)
                return null;

            var normalized = NormalizeMessageLifecyclePayload(broadcastEvent, payload);
            TrackAssistantMessage(normalized.Info);
            return new MessageUpdated { Payload = normalized };
        }

        private MessagePartUpdated? TranslateMessagePartUpdated(BroadcastEvent broadcastEvent)
        {
            var payload = DeserializePayload(broadcastEvent.Payload, ApiJsonContext.Default.MessagePartUpdatedPayload);
            if (payload is null)
                return null;

            var normalized = NormalizeMessagePartUpdatedPayload(broadcastEvent, payload);
            TrackMessagePart(normalized.Part);
            return new MessagePartUpdated { Payload = normalized };
        }

        private static MessagePartDeltaStreamed? TranslateMessagePartDeltaStreamed(BroadcastEvent broadcastEvent)
        {
            var payload = DeserializePayload(broadcastEvent.Payload, ApiJsonContext.Default.MessagePartDeltaStreamedPayload);
            if (payload is null)
                return null;

            return new MessagePartDeltaStreamed
            {
                Payload = payload with { SessionId = ResolveSessionId(broadcastEvent) }
            };
        }

        private static SessionStarted? TranslateSessionStarted(BroadcastEvent broadcastEvent)
        {
            if (broadcastEvent.Payload.ValueKind != JsonValueKind.Object)
                return null;

            var payload = broadcastEvent.Payload;
            var sessionId = ResolveSessionId(broadcastEvent);
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
                        ?? GetBooleanProperty(infoElement, "isHidden")
                }
            };
        }

        private static SessionDeleted TranslateSessionDeleted(BroadcastEvent broadcastEvent)
        {
            var payload = DeserializePayload(broadcastEvent.Payload, ApiJsonContext.Default.SessionDeletedPayload);
            return new SessionDeleted
            {
                Payload = new SessionDeletedPayload
                {
                    SessionId = payload?.SessionId ?? ResolveSessionId(broadcastEvent)
                }
            };
        }

        private DomainEvent? TranslateSessionStatus(BroadcastEvent broadcastEvent)
        {
            var signal = ReadTurnSignal(broadcastEvent.Payload);
            if (signal.Status is null)
                return null;

            if (string.Equals(signal.Status, IdleStatus, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(_activityStatus, IdleStatus, StringComparison.OrdinalIgnoreCase))
                    return null;

                _activityStatus = IdleStatus;
                return new TurnEnded { Payload = CreateTurnEndedPayload(broadcastEvent, signal) };
            }

            if (string.Equals(signal.Status, BusyStatus, StringComparison.OrdinalIgnoreCase)
                || string.Equals(signal.Status, WorkingStatus, StringComparison.OrdinalIgnoreCase))
            {
                var wasIdle = string.Equals(_activityStatus, IdleStatus, StringComparison.OrdinalIgnoreCase);
                _activityStatus = BusyStatus;

                if (!wasIdle)
                {
                    UpdateActiveTurn(signal, ResolveSessionId(broadcastEvent));
                    return null;
                }

                var payload = CreateTurnStartedPayload(broadcastEvent, signal);
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

        private TurnEnded? TranslateSessionIdle(BroadcastEvent broadcastEvent)
        {
            if (string.Equals(_activityStatus, IdleStatus, StringComparison.OrdinalIgnoreCase))
                return null;

            _activityStatus = IdleStatus;
            return new TurnEnded { Payload = CreateTurnEndedPayload(broadcastEvent, ReadTurnSignal(broadcastEvent.Payload)) };
        }

        private static DelegationCreated? TranslateDelegationCreated(BroadcastEvent broadcastEvent)
        {
            var payload = DeserializePayload(broadcastEvent.Payload, ApiJsonContext.Default.DelegationCreatedPayload);
            if (payload is null)
                return null;

            return new DelegationCreated
            {
                Payload = payload with
                {
                    ParentSessionId = string.IsNullOrWhiteSpace(payload.ParentSessionId)
                        ? ResolveSessionId(broadcastEvent)
                        : payload.ParentSessionId
                }
            };
        }

        private static DelegationUpdated? TranslateDelegationUpdated(BroadcastEvent broadcastEvent)
        {
            var payload = DeserializePayload(broadcastEvent.Payload, ApiJsonContext.Default.DelegationUpdatedPayload);
            if (payload is null)
                return null;

            return new DelegationUpdated
            {
                Payload = payload with
                {
                    ParentSessionId = string.IsNullOrWhiteSpace(payload.ParentSessionId)
                        ? ResolveSessionId(broadcastEvent)
                        : payload.ParentSessionId
                }
            };
        }

        private static DelegationCompleted? TranslateDelegationCompleted(BroadcastEvent broadcastEvent)
        {
            var payload = DeserializePayload(broadcastEvent.Payload, ApiJsonContext.Default.DelegationCompletedPayload);
            if (payload is null)
                return null;

            return new DelegationCompleted
            {
                Payload = payload with
                {
                    ParentSessionId = string.IsNullOrWhiteSpace(payload.ParentSessionId)
                        ? ResolveSessionId(broadcastEvent)
                        : payload.ParentSessionId
                }
            };
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
            if (part is StepStartedMessageEventPart stepStarted)
            {
                _activeTurn = new TurnContext(part.SessionId, part.MessageId, stepStarted.Index, null, null, null);
                _nextTurnIndex = Math.Max(_nextTurnIndex, stepStarted.Index + 1);
                return;
            }

            if (part is not StepFinishedMessageEventPart stepFinished)
                return;

            var context = _activeTurn ?? new TurnContext(part.SessionId, part.MessageId, stepFinished.Index, null, null, null);
            _lastAssistantSnapshot = new AssistantTurnSnapshot(
                context with { MessageId = part.MessageId, Index = stepFinished.Index },
                stepFinished.Cost,
                stepFinished.Tokens,
                stepFinished.CompletedAt,
                stepFinished.Reason);
            _nextTurnIndex = Math.Max(_nextTurnIndex, stepFinished.Index + 1);
        }

        private void UpdateActiveTurn(TurnSignal signal, string sessionId)
        {
            if (_activeTurn is null)
                return;

            _activeTurn = new TurnContext(
                sessionId,
                signal.MessageId ?? _activeTurn.MessageId,
                signal.Index ?? _activeTurn.Index,
                signal.Agent ?? _activeTurn.Agent,
                signal.ModelId ?? _activeTurn.ModelId,
                signal.ParentId ?? _activeTurn.ParentId);
        }

        private TurnStartedPayload CreateTurnStartedPayload(BroadcastEvent broadcastEvent, TurnSignal signal)
        {
            var sessionId = ResolveSessionId(broadcastEvent);
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
                ParentId = signal.ParentId ?? _activeTurn?.ParentId ?? lastContext?.ParentId
            };
        }

        private TurnEndedPayload CreateTurnEndedPayload(BroadcastEvent broadcastEvent, TurnSignal signal)
        {
            var sessionId = ResolveSessionId(broadcastEvent);
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
                        Reasoning = signal.Tokens.Reasoning
                    }
                    : completedSnapshot?.Tokens is not null
                        ? new TurnTokenUsage
                        {
                            Input = completedSnapshot.Tokens.Input,
                            Output = completedSnapshot.Tokens.Output,
                            Reasoning = completedSnapshot.Tokens.Reasoning
                        }
                        : null,
                CompletedAt = signal.CompletedAt ?? completedSnapshot?.CompletedAt
            };

            _activeTurn = null;
            return payload;
        }

        private static T? DeserializePayload<T>(JsonElement payload, JsonTypeInfo<T> typeInfo)
            where T : class
        {
            if (payload.ValueKind != JsonValueKind.Object)
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

        private static MessageLifecyclePayload NormalizeMessageLifecyclePayload(BroadcastEvent broadcastEvent, MessageLifecyclePayload payload)
        {
            var sessionId = ResolveSessionId(broadcastEvent);
            var parts = payload.Parts ?? [];
            return payload with
            {
                Info = payload.Info with { SessionId = sessionId },
                Parts = parts.Select(part => NormalizeMessageEventPart(part, sessionId)).ToArray()
            };
        }

        private static MessagePartUpdatedPayload NormalizeMessagePartUpdatedPayload(BroadcastEvent broadcastEvent, MessagePartUpdatedPayload payload)
        {
            var sessionId = ResolveSessionId(broadcastEvent);
            return payload with
            {
                SessionId = sessionId,
                Part = NormalizeMessageEventPart(payload.Part, sessionId)
            };
        }

        private static MessageEventPart NormalizeMessageEventPart(MessageEventPart part, string sessionId)
            => part switch
            {
                TextMessageEventPart text => text with { SessionId = sessionId },
                ReasoningMessageEventPart reasoning => reasoning with { SessionId = sessionId },
                ToolMessageEventPart tool => tool with { SessionId = sessionId },
                FileMessageEventPart file => file with { SessionId = sessionId },
                StepStartedMessageEventPart stepStarted => stepStarted with { SessionId = sessionId },
                StepFinishedMessageEventPart stepFinished => stepFinished with { SessionId = sessionId },
                _ => part
            };

        private static TurnSignal ReadTurnSignal(JsonElement payload)
        {
            if (payload.ValueKind != JsonValueKind.Object)
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

        private static string ResolveSessionId(BroadcastEvent broadcastEvent)
            => TryParseSessionTopic(broadcastEvent.Topic, out var sessionId)
                ? sessionId
                : string.Empty;

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
}

/// <summary>
/// Tracks per-topic buffering and translation state for a WebSocket v2 subscription.
/// </summary>
internal sealed class WebSocketV2SubscriptionState
{
    private readonly object _gate = new();
    private readonly List<BroadcastEvent> _buffer = [];
    private readonly WebSocketV2Protocol.WebSocketV2DomainEventTranslator _translator = new();
    private bool _isReady;

    /// <summary>
    /// Gets a value indicating whether snapshot delivery has completed.
    /// </summary>
    public bool IsReady
    {
        get
        {
            lock (_gate)
                return _isReady;
        }
    }

    /// <summary>
    /// Seeds the translator from the delivered snapshot.
    /// </summary>
    public void Initialize(SessionSnapshot snapshot)
    {
        lock (_gate)
            _translator.Initialize(snapshot);
    }

    /// <summary>
    /// Buffers a live event that arrived before the snapshot was delivered.
    /// </summary>
    public void Buffer(BroadcastEvent broadcastEvent)
    {
        lock (_gate)
            _buffer.Add(broadcastEvent);
    }

    /// <summary>
    /// Drains buffered events that are newer than the supplied snapshot watermark.
    /// </summary>
    public IReadOnlyList<BroadcastEvent> DrainBuffered(long? snapshotEventIdWatermark)
    {
        lock (_gate)
        {
            if (_buffer.Count == 0)
                return [];

            var pending = new List<BroadcastEvent>(_buffer.Count);
            foreach (var bufferedEvent in _buffer)
            {
                if (!bufferedEvent.EventId.HasValue
                    || !snapshotEventIdWatermark.HasValue
                    || bufferedEvent.EventId.Value > snapshotEventIdWatermark.Value)
                {
                    pending.Add(bufferedEvent);
                }
            }

            _buffer.Clear();
            return pending;
        }
    }

    /// <summary>
    /// Marks the subscription ready once no buffered events remain.
    /// </summary>
    public bool TryMarkReady()
    {
        lock (_gate)
        {
            if (_buffer.Count > 0)
                return false;

            _isReady = true;
            return true;
        }
    }

    /// <summary>
    /// Translates a raw broadcast event into a domain event.
    /// </summary>
    public DomainEvent? Translate(BroadcastEvent broadcastEvent)
    {
        lock (_gate)
        {
            var translatedEvent = _translator.Translate(broadcastEvent);
            return translatedEvent ?? broadcastEvent.DomainEvent;
        }
    }
}
