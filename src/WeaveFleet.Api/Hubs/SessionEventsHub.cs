using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Api.Hubs;

/// <summary>
/// SignalR hub for real-time session events.
/// Each connection subscribes to a per-connection event channel from <see cref="IEventBroadcaster"/>
/// and filters events by the connection's subscribed topics.
/// </summary>
public class SessionEventsHub : Hub
{
    private readonly IEventBroadcaster _broadcaster;
    private readonly IUserContext _userContext;
    private readonly ILogger<SessionEventsHub> _logger;
    private readonly ISessionMessageProxy _proxy;
    private readonly IHubContext<SessionEventsHub> _hubContext;

    // Per-connection state: subscribed topics
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ConnectionTopics = new();

    // Per-connection state: pump cancellation
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> ConnectionCancellations = new();

    // Maps parent topic to set of child topics auto-subscribed via delegation
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> ParentChildTopics = new();

    // LoggerMessage delegates for high-performance logging
    private static readonly Action<ILogger, string, Exception?> LogPumpCancelled =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "HubPumpCancelled"),
            "Event pump cancelled for connection {ConnectionId}");

    private static readonly Action<ILogger, string, Exception> LogPumpFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2, "HubPumpFailed"),
            "Event pump failed for connection {ConnectionId}");

    private static readonly Action<ILogger, string, string, Exception?> LogSubscribed =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(3, "HubSubscribed"),
            "Connection {ConnectionId} subscribed to {Topic}");

    private static readonly Action<ILogger, string, string, Exception?> LogUnsubscribed =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(4, "HubUnsubscribed"),
            "Connection {ConnectionId} unsubscribed from {Topic}");

    private static readonly Action<ILogger, string, Exception> LogSendFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5, "HubSendFailed"),
            "Failed to send event to connection {ConnectionId}");

    private static readonly Action<ILogger, string, string, Exception?> LogAutoSubscribed =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(6, "HubAutoSubscribed"),
            "Auto-subscribed connection {ConnectionId} to child session {ChildTopic} after delegation");

    public SessionEventsHub(
        IEventBroadcaster broadcaster,
        IUserContext userContext,
        ILogger<SessionEventsHub> logger,
        ISessionMessageProxy proxy,
        IHubContext<SessionEventsHub> hubContext)
    {
        _broadcaster = broadcaster;
        _userContext = userContext;
        _logger = logger;
        _proxy = proxy;
        _hubContext = hubContext;
    }

    /// <summary>
    /// Called when a client connects. Starts the per-connection event pump.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var connectionId = Context.ConnectionId;

        // Initialize per-connection topic filter set
        var topics = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        ConnectionTopics[connectionId] = topics;

        // Create per-connection cancellation token
        var cts = new CancellationTokenSource();
        ConnectionCancellations[connectionId] = cts;

        // Start background event pump for this connection
        _ = Task.Run(async () =>
        {
            try
            {
                await PumpEventsAsync(connectionId, cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    LogPumpCancelled(_logger, connectionId, null);
            }
            catch (Exception ex)
            {
                LogPumpFailed(_logger, connectionId, ex);
            }
        }, cts.Token);

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called when a client disconnects. Cancels the event pump and cleans up resources.
    /// </summary>
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var connectionId = Context.ConnectionId;

        // Cancel the pump task
        if (ConnectionCancellations.TryRemove(connectionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        // Clean up parent-child topic mappings for this connection's topics
        if (ConnectionTopics.TryGetValue(connectionId, out var topics))
        {
            foreach (var topic in topics.Keys)
            {
                // Remove parent-child mappings where this topic is a parent
                ParentChildTopics.TryRemove(topic, out _);
            }
        }

        // Remove topic filter set
        ConnectionTopics.TryRemove(connectionId, out _);

        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribe to a session topic. Adds the connection to a SignalR group and returns
    /// an atomic snapshot that merges persisted messages with in-flight streaming state.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>A snapshot with persisted messages overlaid with in-flight text deltas.</returns>
    public async Task<SessionSnapshot> SubscribeToSessionAsync(string sessionId)
    {
        var connectionId = Context.ConnectionId;
        var topic = $"session:{sessionId}";

        // Add connection to SignalR group and topic filter
        await AddTopicSubscriptionAsync(connectionId, topic, CancellationToken.None);

        if (_logger.IsEnabled(LogLevel.Debug))
            LogSubscribed(_logger, connectionId, topic, null);

        // Build atomic snapshot: persisted messages + in-flight state
        var snapshot = await BuildAtomicSnapshotAsync(sessionId);

        // Restore child topic subscriptions from active delegations
        foreach (var delegation in snapshot.Delegations)
        {
            if (delegation.ChildSessionId is not null
                && (delegation.Status == "running" || delegation.Status == "pending"))
            {
                var childTopic = $"session:{delegation.ChildSessionId}";
                await AddTopicSubscriptionAsync(connectionId, childTopic, CancellationToken.None);

                var childTopics = ParentChildTopics.GetOrAdd(topic, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                childTopics[childTopic] = 0;

                if (_logger.IsEnabled(LogLevel.Debug))
                    LogAutoSubscribed(_logger, connectionId, childTopic, null);
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Adds a topic subscription for a connection by updating the filter set and SignalR group.
    /// </summary>
    private async Task AddTopicSubscriptionAsync(string connectionId, string topic, CancellationToken ct)
    {
        // Add connection to SignalR group (events start flowing immediately)
        await _hubContext.Groups.AddToGroupAsync(connectionId, topic, ct);

        // Add topic to connection's filter set
        if (ConnectionTopics.TryGetValue(connectionId, out var topics))
        {
            topics[topic] = 0;
        }
    }

    /// <summary>
    /// Builds an atomic snapshot by delegating to the proxy.
    /// The proxy handles fetching messages from opencode's API (if available) or persisted storage,
    /// along with delegations and activity status.
    /// </summary>
    private async Task<SessionSnapshot> BuildAtomicSnapshotAsync(string sessionId)
    {
        // Delegate to proxy: fetches messages from opencode API or persisted storage,
        // merges delegations from Fleet's database, and includes activity status
        var snapshot = await _proxy.GetSnapshotAsync(sessionId, pageSize: 100, cursor: null);

        // Remove lastEventId from snapshot (client dedup watermark no longer needed)
        return snapshot with { LastEventId = null };
    }

    /// <summary>
    /// Subscribe to the global "sessions" topic to receive activity_status events for all sessions.
    /// This is used by the session list to update activity badges in real-time.
    /// </summary>
    public async Task SubscribeToSessionsTopicAsync()
    {
        var connectionId = Context.ConnectionId;
        const string topic = "sessions";

        // Add connection to SignalR group and topic filter
        await AddTopicSubscriptionAsync(connectionId, topic, CancellationToken.None);

        if (_logger.IsEnabled(LogLevel.Debug))
            LogSubscribed(_logger, connectionId, topic, null);
    }

    /// <summary>
    /// Unsubscribe from the global "sessions" topic.
    /// </summary>
    public async Task UnsubscribeFromSessionsTopicAsync()
    {
        var connectionId = Context.ConnectionId;
        const string topic = "sessions";

        // Remove connection from SignalR group (idempotent)
        await Groups.RemoveFromGroupAsync(connectionId, topic);

        // Remove topic from connection's filter set (idempotent)
        if (ConnectionTopics.TryGetValue(connectionId, out var topics))
        {
            topics.TryRemove(topic, out _);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
            LogUnsubscribed(_logger, connectionId, topic, null);
    }

    /// <summary>
    /// Unsubscribe from a session topic. Removes the connection from the SignalR group and updates the topic filter.
    /// Also cleans up any child topics that were auto-subscribed via delegation.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    public async Task UnsubscribeFromSessionAsync(string sessionId)
    {
        var connectionId = Context.ConnectionId;
        var topic = $"session:{sessionId}";

        // Remove connection from SignalR group (idempotent)
        await Groups.RemoveFromGroupAsync(connectionId, topic);

        // Remove topic from connection's filter set (idempotent)
        if (ConnectionTopics.TryGetValue(connectionId, out var topics))
        {
            topics.TryRemove(topic, out _);
        }

        // Clean up any child topics that were auto-subscribed via delegation
        if (ParentChildTopics.TryGetValue(topic, out var childTopics))
        {
            foreach (var childTopic in childTopics.Keys)
            {
                // Remove child topic from SignalR group
                await Groups.RemoveFromGroupAsync(connectionId, childTopic);

                // Remove child topic from connection's filter set
                if (topics is not null)
                {
                    topics.TryRemove(childTopic, out _);
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                    LogUnsubscribed(_logger, connectionId, childTopic, null);
            }
        }

        if (_logger.IsEnabled(LogLevel.Debug))
            LogUnsubscribed(_logger, connectionId, topic, null);
    }

    /// <summary>
    /// Background task that pumps events from the broadcaster to the SignalR client.
    /// Events are filtered by the connection's subscribed topics.
    /// </summary>
    private async Task PumpEventsAsync(string connectionId, CancellationToken ct)
    {
        // Subscribe to all topics with user scope — broadcaster delivers only matching events
        var allTopics = new[] { "*" };

        await foreach (var evt in _broadcaster.SubscribeAsync(allTopics, _userContext.UserId, ct))
        {
            // Check if this connection is subscribed to the event's topic
            if (!ConnectionTopics.TryGetValue(connectionId, out var topics))
                break; // Connection was removed

            if (!topics.ContainsKey(evt.Topic))
                continue; // Not subscribed to this topic

            // Auto-subscribe to child session when delegation occurs
            if (evt.Type == "delegation.updated" && evt.Payload.TryGetProperty("childSessionId", out var childSessionIdProp))
            {
                var childSessionId = childSessionIdProp.GetString();
                if (!string.IsNullOrEmpty(childSessionId))
                {
                    var childTopic = $"session:{childSessionId}";
                    var parentTopic = evt.Topic;
                    
                    // Add child topic subscription
                    await AddTopicSubscriptionAsync(connectionId, childTopic, ct);
                    
                    // Track parent-child relationship for cleanup
                    var childTopics = ParentChildTopics.GetOrAdd(parentTopic, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                    childTopics[childTopic] = 0;
                    
                    if (_logger.IsEnabled(LogLevel.Debug))
                        LogAutoSubscribed(_logger, connectionId, childTopic, null);
                }
            }

            // When a DomainEvent is attached, prefer its discriminator type over the raw harness type.
            // Raw harness types (e.g. "session.status") differ from domain event types (e.g. "turn.started")
            // and the client reducer only handles domain event types.
            var wireType = evt.DomainEvent is not null
                ? ResolveDomainEventType(evt.DomainEvent)
                : evt.Type;

            // Sanitize the payload before sending to client
            var sanitizedPayload = ClientPayloadSanitizer.SanitizeEventPayload(wireType, evt.Payload);
            if (!sanitizedPayload.HasValue)
                continue;

            // Build the wire envelope matching the client's WebSocketEvent shape:
            // { type: string, eventId?: number, properties: Record<string, any> }
            // Serialize as raw JSON to avoid type-system mismatch with SignalR's JSON serializer.
            var clientEventJson = JsonSerializer.SerializeToElement(new ClientEvent
            {
                Type = wireType,
                EventId = evt.EventId,
                Properties = sanitizedPayload.Value
            }, ApiJsonContext.Default.ClientEvent);

            // Send event to the caller via IHubContext (safe to use outside hub method scope)
            try
            {
                await _hubContext.Clients.Client(connectionId).SendAsync(
                    "Event",
                    evt.Topic,
                    evt.EventId ?? 0,
                    clientEventJson,
                    ct);
            }
            catch (Exception ex)
            {
                LogSendFailed(_logger, connectionId, ex);
                break;
            }
        }
    }

    /// <summary>
    /// Maps a <see cref="DomainEvent"/> instance to its JSON type discriminator string.
    /// This is the value used in <c>[JsonDerivedType(typeof(T), "...")]</c> on <see cref="DomainEvent"/>.
    /// </summary>
    private static string ResolveDomainEventType(DomainEvent domainEvent) => domainEvent switch
    {
        SessionStarted          => "session.started",
        SessionIdled            => "session.idled",
        SessionStopped          => "session.stopped",
        SessionDeleted          => "session.deleted",
        SessionArchived         => "session.archived",
        TurnStarted             => "turn.started",
        TurnEnded               => "turn.ended",
        MessageCreated          => "message.created",
        MessageUpdated          => "message.updated",
        MessagePartUpdated      => "message.part.updated",
        MessagePartDeltaStreamed => "message.part.delta.streamed",
        DelegationCreated       => "delegation.created",
        DelegationUpdated       => "delegation.updated",
        DelegationCompleted     => "delegation.completed",
        _ => domainEvent.GetType().Name,
    };
}

/// <summary>
/// Wire envelope sent to the client matching the <c>WebSocketEvent</c> TypeScript interface.
/// Shape: <c>{ type: string, eventId?: number, properties: Record&lt;string, any&gt; }</c>
/// </summary>
internal sealed record ClientEvent
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("eventId")]
    public long? EventId { get; init; }

    [JsonPropertyName("properties")]
    public required JsonElement Properties { get; init; }
}
