using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

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

    public static WebApplication MapWebSocketEndpoints(this WebApplication app)
    {
        app.Map("/ws", HandleWebSocketAsync);
        return app;
    }

    private static async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var logger = context.RequestServices.GetRequiredService<ILogger<WebApplication>>();
        var broadcaster = context.RequestServices.GetRequiredService<IEventBroadcaster>();

        if (logger.IsEnabled(LogLevel.Debug))
            LogConnected(logger, context.Connection.RemoteIpAddress?.ToString(), null);

        // Current subscribed topics for this connection
        var subscribedTopics = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        // Pump events to the WebSocket while it is open
        var sendTask = PumpEventsAsync(webSocket, broadcaster, subscribedTopics, cts.Token);

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

                    var response = JsonSerializer.Serialize(new { type = "subscribed", topics = subscribedTopics });
                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await webSocket.SendAsync(new ArraySegment<byte>(responseBytes),
                        WebSocketMessageType.Text, endOfMessage: true, cts.Token);
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
        await sendTask.ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Debug))
            LogDisconnected(logger, null);
    }

    private static async Task PumpEventsAsync(
        WebSocket webSocket,
        IEventBroadcaster broadcaster,
        List<string> subscribedTopics,
        CancellationToken ct)
    {
        // Subscribe to all topics — we filter on the topic list at broadcast time
        // Use a wildcard "fleet.*" subscription by subscribing to all topics
        // The broadcaster delivers only what was published; we re-check topics here
        var allTopics = new[] { "sessions", "instances", "activity" };

        await foreach (var evt in broadcaster.SubscribeAsync(allTopics, ct))
        {
            bool inScope;
            lock (subscribedTopics)
                inScope = subscribedTopics.Count == 0 || subscribedTopics.Contains(evt.Topic);

            if (!inScope)
                continue;

            if (webSocket.State != WebSocketState.Open)
                break;

            var json = JsonSerializer.Serialize(new
            {
                type = evt.Type,
                topic = evt.Topic,
                payload = evt.Payload,
                timestamp = evt.Timestamp.ToUnixTimeMilliseconds()
            });

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
}
