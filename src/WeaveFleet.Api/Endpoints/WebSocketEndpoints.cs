using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

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

        if (logger.IsEnabled(LogLevel.Debug))
        {
            LogConnected(logger, context.Connection.RemoteIpAddress?.ToString(), null);
        }

        var buffer = new byte[4096];

        while (webSocket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
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
                    // Acknowledge subscription with the same topics back
                    var topics = doc.RootElement.TryGetProperty("topics", out var topicsProp)
                        ? topicsProp.EnumerateArray().Select(t => t.GetString()).ToArray()
                        : [];

                    var response = JsonSerializer.Serialize(new
                    {
                        type = "subscribed",
                        topics
                    });

                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(responseBytes),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        CancellationToken.None);
                }
                // "unsubscribe" — no response required per protocol
            }
        }

        if (logger.IsEnabled(LogLevel.Debug))
        {
            LogDisconnected(logger, null);
        }
    }
}
