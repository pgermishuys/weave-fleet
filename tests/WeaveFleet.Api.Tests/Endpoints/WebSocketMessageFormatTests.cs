using System.Text.Json;
using WeaveFleet.Api;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Tests that the WebSocket event envelope format matches the frontend protocol.
/// Frontend expects: { type: "event", topic: "...", data: { type: "...", properties: {...} } }
/// See use-weave-socket.ts:116 and use-session-events.ts:361.
/// </summary>
public sealed class WebSocketMessageFormatTests
{
    private static string SerializeEnvelope(BroadcastEvent evt)
    {
        // Mirror the serialization in WebSocketEndpoints.PumpEventsAsync
        return JsonSerializer.Serialize(
            new WsEventPayload(
                "event",
                evt.Topic,
                new WsEventDataPayload(evt.Type, evt.EventId, evt.EventId, evt.Payload)),
            ApiJsonContext.Default.WsEventPayload);
    }

    [Fact]
    public void Envelope_has_type_event_not_the_event_type()
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "hello" });
        var evt = new BroadcastEvent("session:abc", "message.updated", payload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("type").GetString().ShouldBe("event");
    }

    [Fact]
    public void Envelope_data_type_equals_original_event_type()
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "hello" });
        var evt = new BroadcastEvent("session:abc", "message.updated", payload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement
            .GetProperty("data")
            .GetProperty("type")
            .GetString()
            .ShouldBe("message.updated");
    }

    [Fact]
    public void Envelope_includes_sequence_number_when_present()
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "hello" });
        var evt = new BroadcastEvent("session:abc", "message.updated", payload, DateTimeOffset.UtcNow, 42);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement
            .GetProperty("data")
            .GetProperty("sequenceNumber")
            .GetInt64()
            .ShouldBe(42);
    }

    [Fact]
    public void Envelope_includes_event_id_when_present()
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "hello" });
        var evt = new BroadcastEvent("session:abc", "message.updated", payload, DateTimeOffset.UtcNow, 42);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement
            .GetProperty("data")
            .GetProperty("eventId")
            .GetInt64()
            .ShouldBe(42);
    }

    [Fact]
    public void Envelope_topic_equals_broadcast_topic()
    {
        var payload = JsonSerializer.SerializeToElement(new { id = "s1" });
        var evt = new BroadcastEvent("session:fleet-123", "session.status", payload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("topic").GetString().ShouldBe("session:fleet-123");
    }

    [Fact]
    public void Envelope_data_properties_matches_payload_without_double_encoding()
    {
        var originalObject = new { text = "hello world", count = 42 };
        var payload = JsonSerializer.SerializeToElement(originalObject);
        var evt = new BroadcastEvent("session:abc", "message.updated", payload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        var properties = doc.RootElement.GetProperty("data").GetProperty("properties");

        // properties should be the payload object, not a double-encoded string
        properties.ValueKind.ShouldBe(JsonValueKind.Object);
        properties.GetProperty("text").GetString().ShouldBe("hello world");
        properties.GetProperty("count").GetInt32().ShouldBe(42);
    }

    [Fact]
    public void Envelope_has_no_top_level_payload_or_timestamp_fields()
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "hello" });
        var evt = new BroadcastEvent("session:abc", "message.updated", payload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Frontend does NOT use payload or timestamp at top level — verify they're absent
        root.TryGetProperty("payload", out _).ShouldBeFalse(
            "Top-level 'payload' field must not be present (use data.properties instead)");
        root.TryGetProperty("timestamp", out _).ShouldBeFalse(
            "Top-level 'timestamp' field must not be present");
    }

    [Fact]
    public void Envelope_with_null_payload_produces_empty_object_properties()
    {
        // The relay converts null Payload to JsonSerializer.SerializeToElement(new {}) = {}
        // This test verifies the envelope handles that gracefully
        var emptyPayload = JsonSerializer.SerializeToElement(new { });
        var evt = new BroadcastEvent("session:abc", "session.status", emptyPayload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        var properties = doc.RootElement.GetProperty("data").GetProperty("properties");
        properties.ValueKind.ShouldBe(JsonValueKind.Object);
        properties.EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public void Full_envelope_structure_matches_frontend_protocol()
    {
        var payload = JsonSerializer.SerializeToElement(new { messageId = "msg-1", role = "assistant" });
        var evt = new BroadcastEvent("session:fleet-abc", "message.updated", payload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Top-level structure
        root.GetProperty("type").GetString().ShouldBe("event");
        root.GetProperty("topic").GetString().ShouldBe("session:fleet-abc");

        // data object
        var data = root.GetProperty("data");
        data.GetProperty("type").GetString().ShouldBe("message.updated");
        var props = data.GetProperty("properties");
        props.GetProperty("messageId").GetString().ShouldBe("msg-1");
        props.GetProperty("role").GetString().ShouldBe("assistant");
    }
}
