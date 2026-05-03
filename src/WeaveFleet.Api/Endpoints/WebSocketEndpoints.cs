using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WeaveFleet.Api;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class WebSocketEndpoints
{
    private static readonly Action<ILogger, string?, Exception?> LogConnected =
        LoggerMessage.Define<string?>(LogLevel.Debug, new EventId(1, "WsConnected"),
            "WebSocket connection established from {RemoteIp}");

    private static readonly Action<ILogger, string, Exception?> LogInvalidJson =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "WsInvalidJson"),
            "Received invalid JSON over WebSocket: {Message}");

    private static readonly Action<ILogger, Exception?> LogDisconnected =
        LoggerMessage.Define(LogLevel.Debug, new EventId(3, "WsDisconnected"),
            "WebSocket connection closed");

    private static readonly Action<ILogger, Exception?> LogPumpCancelled =
        LoggerMessage.Define(LogLevel.Debug, new EventId(4, "WsPumpCancelled"),
            "WebSocket event pump cancelled");

    public static IEndpointRouteBuilder MapWebSocketEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map("/ws", HandleWebSocketAsync);
        return app;
    }

    private static async Task HandleWebSocketAsync(HttpContext context)
    {
        var fleetOptions = context.RequestServices.GetRequiredService<Application.Configuration.FleetOptions>();

        if (!IsOriginAllowed(context, fleetOptions))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var logger = context.RequestServices.GetRequiredService<ILogger<WebApplication>>();
        var broadcaster = context.RequestServices.GetRequiredService<IEventBroadcaster>();
        var userContext = context.RequestServices.GetRequiredService<IUserContext>();

        if (logger.IsEnabled(LogLevel.Debug))
            LogConnected(logger, context.Connection.RemoteIpAddress?.ToString(), null);

        // Current subscribed topics for this connection
        var subscribedTopics = new List<string>();
        var lifetime = context.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);

        // Pump events to the WebSocket while it is open — scoped to the authenticated user
        var sendTask = PumpEventsAsync(webSocket, broadcaster, subscribedTopics, userContext.UserId, cts.Token);

        var buffer = new byte[4096];

        while (webSocket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var messageText = Encoding.UTF8.GetString(buffer, 0, result.Count);

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(messageText);
            }
            catch (JsonException)
            {
                LogInvalidJson(logger, messageText, null);
                continue;
            }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                    continue;

                var messageType = typeProp.GetString();

                if (messageType == "subscribe")
                {
                    var newTopics = doc.RootElement.TryGetProperty("topics", out var topicsProp)
                        ? topicsProp.EnumerateArray()
                              .Select(t => t.GetString())
                              .Where(t => t is not null)
                              .Select(t => t!)
                              .ToList()
                        : [];

                    lock (subscribedTopics)
                    {
                        foreach (var t in newTopics)
                            if (!subscribedTopics.Contains(t))
                                subscribedTopics.Add(t);
                    }

                    var response = JsonSerializer.Serialize(new WsSubscribedPayload("subscribed", subscribedTopics), ApiJsonContext.Default.WsSubscribedPayload);
                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await webSocket.SendAsync(new ArraySegment<byte>(responseBytes),
                        WebSocketMessageType.Text, endOfMessage: true, cts.Token);

                    // When subscribing to the "activity" topic, immediately send the current
                    // activity state for all tracked sessions so page refresh shows correct status.
                    if (newTopics.Contains("activity"))
                    {
                        var activityTracker = context.RequestServices.GetRequiredService<SessionActivityTracker>();
                        foreach (var snapshot in activityTracker.GetAll().Values)
                        {
                            // Respect user scoping — only send snapshots owned by this subscriber
                            if (userContext.UserId is not null
                                && snapshot.UserId is not null
                                && !string.Equals(snapshot.UserId, userContext.UserId, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            // Use derived effective status so that a parent session shows "busy"
                            // when any of its delegated child sessions are busy.
                            var effectiveActivityStatus =
                                activityTracker.GetEffectiveActivityStatus(snapshot.FleetSessionId)
                                ?? snapshot.ActivityStatus;

                            var props = JsonSerializer.SerializeToElement(
                                new WsActivityStatusProperties(snapshot.FleetSessionId, effectiveActivityStatus),
                                ApiJsonContext.Default.WsActivityStatusProperties);
                            var initialEvent = JsonSerializer.Serialize(
                                new WsEventPayload("event", "activity",
                                    new WsEventDataPayload("activity_status", null, props)),
                                ApiJsonContext.Default.WsEventPayload);
                            var initialBytes = Encoding.UTF8.GetBytes(initialEvent);
                            await webSocket.SendAsync(new ArraySegment<byte>(initialBytes),
                                WebSocketMessageType.Text, endOfMessage: true, cts.Token);
                        }
                    }
                }
                else if (messageType == "unsubscribe")
                {
                    var removeTopics = doc.RootElement.TryGetProperty("topics", out var topicsProp)
                        ? topicsProp.EnumerateArray()
                              .Select(t => t.GetString())
                              .Where(t => t is not null)
                              .Select(t => t!)
                              .ToList()
                        : [];

                    lock (subscribedTopics)
                        subscribedTopics.RemoveAll(removeTopics.Contains);
                }
            }
        }

        await cts.CancelAsync();

        try
        {
            await sendTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                LogPumpCancelled(logger, null);
        }

        if (logger.IsEnabled(LogLevel.Debug))
            LogDisconnected(logger, null);
    }

    private static async Task PumpEventsAsync(
        WebSocket webSocket,
        IEventBroadcaster broadcaster,
        List<string> subscribedTopics,
        string? subscriberUserId,
        CancellationToken ct)
    {
        // Subscribe to all topics with user scope — broadcaster delivers only matching events
        var allTopics = new[] { "*" };

        try
        {
            await foreach (var evt in broadcaster.SubscribeAsync(allTopics, subscriberUserId, ct))
            {
                bool inScope;
                lock (subscribedTopics)
                    inScope = subscribedTopics.Contains(evt.Topic)
                        || (evt.Topic == "sessions" && subscribedTopics.Contains("activity"));

                if (!inScope)
                    continue;

                if (webSocket.State != WebSocketState.Open)
                    break;

                var sanitizedPayload = ClientPayloadSanitizer.SanitizeEventPayload(evt.Type, evt.Payload);
                if (!sanitizedPayload.HasValue)
                    continue;

                var json = JsonSerializer.Serialize(
                    new WsEventPayload("event", evt.Topic,
                        new WsEventDataPayload(evt.Type, evt.SequenceNumber, sanitizedPayload.Value)),
                    ApiJsonContext.Default.WsEventPayload);

                var bytes = Encoding.UTF8.GetBytes(json);
                try
                {
                    await webSocket.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, endOfMessage: true, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
    }

    private static bool IsOriginAllowed(HttpContext context, Application.Configuration.FleetOptions fleetOptions)
    {
        if (!fleetOptions.Auth.Enabled)
            return true;

        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
            return false;

        if (fleetOptions.Auth.AllowedOrigins.Length == 0)
            return false;

        return fleetOptions.Auth.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }
}
#pragma warning restore IL2026
