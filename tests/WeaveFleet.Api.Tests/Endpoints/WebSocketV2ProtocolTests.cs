using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class WebSocketV2ProtocolTests
{
    [Fact]
    public async Task send_event_async_emits_exactly_one_visible_turn_ended_for_no_tool_turn_when_both_idle_signals_arrive()
    {
        using var webSocket = new RecordingWebSocket();
        using var sendLock = new SemaphoreSlim(1, 1);
        var subscriptionState = CreateInitializedSubscriptionState();

        await WebSocketV2Protocol.SendEventAsync(
            webSocket,
            sendLock,
            CreateStatusEvent("busy", messageId: "msg-no-tool", index: 7),
            subscriptionState,
            CancellationToken.None);
        await WebSocketV2Protocol.SendEventAsync(
            webSocket,
            sendLock,
            CreateStatusEvent("idle"),
            subscriptionState,
            CancellationToken.None);
        await WebSocketV2Protocol.SendEventAsync(
            webSocket,
            sendLock,
            new BroadcastEvent("session:fleet-1", EventTypes.SessionIdle, JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow),
            subscriptionState,
            CancellationToken.None);

        var turnEndedMessages = webSocket.Messages
            .Select(ParseDomainEventType)
            .Where(eventType => string.Equals(eventType, "turn.ended", StringComparison.Ordinal))
            .ToArray();

        turnEndedMessages.Length.ShouldBe(1);
    }

    [Fact]
    public async Task send_event_async_does_not_emit_turn_ended_for_idle_signal_without_active_turn()
    {
        using var webSocket = new RecordingWebSocket();
        using var sendLock = new SemaphoreSlim(1, 1);
        var subscriptionState = CreateInitializedSubscriptionState();

        await WebSocketV2Protocol.SendEventAsync(
            webSocket,
            sendLock,
            new BroadcastEvent("session:fleet-1", EventTypes.SessionIdle, JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow),
            subscriptionState,
            CancellationToken.None);

        webSocket.Messages.ShouldBeEmpty();
    }

    private static WebSocketV2SubscriptionState CreateInitializedSubscriptionState()
    {
        var subscriptionState = new WebSocketV2SubscriptionState();
        subscriptionState.Initialize(new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = "fleet-1",
                Title = "Test session",
                Status = "running"
            },
            ActivityStatus = "idle",
            Messages = [],
            Delegations = []
        });

        return subscriptionState;
    }

    private static BroadcastEvent CreateStatusEvent(string status, string? messageId, int? index)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            status = new
            {
                type = status,
                messageID = messageId,
                index
            }
        });

        return new BroadcastEvent("session:fleet-1", EventTypes.SessionStatus, payload, DateTimeOffset.UtcNow);
    }

    private static BroadcastEvent CreateStatusEvent(string status)
    {
        var payload = JsonSerializer.SerializeToElement(new { status = new { type = status } });
        return new BroadcastEvent("session:fleet-1", EventTypes.SessionStatus, payload, DateTimeOffset.UtcNow);
    }

    private static string? ParseDomainEventType(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement
            .GetProperty("data")
            .GetProperty("type")
            .GetString();
    }

    private sealed class RecordingWebSocket : WebSocket
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
            => Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _messages.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
    }
}
