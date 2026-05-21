using System.Text.Json;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class WebSocketV2SubscriptionStateTests
{
    [Fact]
    public void drain_buffered_drops_event_ids_at_or_below_snapshot_watermark_as_snapshot_included()
    {
        var state = new WebSocketV2SubscriptionState();
        var beforeWatermark = CreateEvent(9, "before-watermark");
        var atWatermark = CreateEvent(10, "at-watermark");
        var afterWatermark = CreateEvent(11, "after-watermark");

        state.Buffer(beforeWatermark);
        state.Buffer(atWatermark);
        state.Buffer(afterWatermark);

        var drained = state.DrainBuffered(10);

        drained.ShouldBe([afterWatermark]);
    }

    [Fact]
    public void drain_buffered_preserves_arrival_order_for_event_ids_above_snapshot_watermark()
    {
        var state = new WebSocketV2SubscriptionState();
        var firstNewerEvent = CreateEvent(12, "first-newer-event");
        var secondNewerEvent = CreateEvent(11, "second-newer-event");
        var thirdNewerEvent = CreateEvent(14, "third-newer-event");

        state.Buffer(firstNewerEvent);
        state.Buffer(secondNewerEvent);
        state.Buffer(thirdNewerEvent);

        var drained = state.DrainBuffered(10);

        drained.ShouldBe([firstNewerEvent, secondNewerEvent, thirdNewerEvent]);
    }

    [Fact]
    public void drain_buffered_preserves_null_event_id_events_in_arrival_order()
    {
        var state = new WebSocketV2SubscriptionState();
        var droppedSnapshotEvent = CreateEvent(10, "snapshot-included-event");
        var firstNullSequenceEvent = CreateEvent(null, "first-null-sequence-event");
        var newerSequencedEvent = CreateEvent(11, "newer-sequenced-event");
        var secondNullSequenceEvent = CreateEvent(null, "second-null-sequence-event");

        state.Buffer(droppedSnapshotEvent);
        state.Buffer(firstNullSequenceEvent);
        state.Buffer(newerSequencedEvent);
        state.Buffer(secondNullSequenceEvent);

        var drained = state.DrainBuffered(10);

        drained.ShouldBe([firstNullSequenceEvent, newerSequencedEvent, secondNullSequenceEvent]);
    }

    [Fact]
    public void subscribe_snapshot_race_delivers_every_durable_event_once_across_snapshot_and_pending_drain()
    {
        const string sessionId = "snapshot-race-session";
        var state = new WebSocketV2SubscriptionState();
        var emittedDuringSubscribe = new[]
        {
            CreateMessageUpdatedEvent(sessionId, 101, "message-included-by-snapshot-101"),
            CreateMessageUpdatedEvent(sessionId, 102, "message-included-by-snapshot-102"),
            CreateMessageUpdatedEvent(sessionId, 103, "message-drained-after-snapshot-103"),
            CreateMessageUpdatedEvent(sessionId, 104, "message-drained-after-snapshot-104")
        };

        foreach (var emittedEvent in emittedDuringSubscribe)
        {
            state.Buffer(emittedEvent);
        }

        var snapshot = new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "Snapshot race session",
                Status = "active"
            },
            ActivityStatus = "idle",
            LastEventId = 102,
            Messages =
            [
                CreateMessageLifecyclePayload(sessionId, "message-included-by-snapshot-101"),
                CreateMessageLifecyclePayload(sessionId, "message-included-by-snapshot-102")
            ],
            Delegations = [],
            HasMore = false,
            Cursor = null
        };

        state.Initialize(snapshot);
        var pendingEvents = state.DrainBuffered(snapshot.LastEventId);

        var snapshotMessageIds = snapshot.Messages.Select(message => message.Info.Id).ToArray();
        var pendingMessageIds = pendingEvents.Select(GetMessageId).ToArray();
        var deliveredMessageIds = snapshotMessageIds.Concat(pendingMessageIds).ToArray();
        var expectedMessageIds = emittedDuringSubscribe.Select(GetMessageId).ToArray();

        deliveredMessageIds.ShouldBe(expectedMessageIds, ignoreOrder: true);
        deliveredMessageIds.Length.ShouldBe(deliveredMessageIds.Distinct(StringComparer.Ordinal).Count());
        pendingMessageIds.ShouldBe(["message-drained-after-snapshot-103", "message-drained-after-snapshot-104"]);
    }

    private static BroadcastEvent CreateEvent(long? eventId, string id)
    {
        var payload = JsonSerializer.SerializeToElement(new { id });

        return new BroadcastEvent(
            "session:test-session",
            "message.updated",
            payload,
            DateTimeOffset.UtcNow,
            eventId,
            null);
    }

    private static BroadcastEvent CreateMessageUpdatedEvent(string sessionId, long eventId, string messageId)
    {
        var payload = JsonSerializer.SerializeToElement(CreateMessageLifecyclePayload(sessionId, messageId));

        return new BroadcastEvent(
            $"session:{sessionId}",
            EventTypes.MessageUpdated,
            payload,
            DateTimeOffset.UtcNow,
            eventId,
            null);
    }

    private static MessageLifecyclePayload CreateMessageLifecyclePayload(string sessionId, string messageId)
        => new()
        {
            Info = new MessageEventInfo
            {
                Id = messageId,
                Role = "assistant",
                SessionId = sessionId,
                Time = new MessageEventTime
                {
                    Created = 1_700_000_000_000
                }
            },
            Parts = []
        };

    private static string GetMessageId(BroadcastEvent broadcastEvent)
        => broadcastEvent.Payload.GetProperty("Info").GetProperty("Id").GetString()
            ?? throw new InvalidOperationException("Expected buffered message event to include info.id.");
}
