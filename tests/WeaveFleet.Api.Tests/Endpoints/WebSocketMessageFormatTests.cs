using System.Text.Json;
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
        return JsonSerializer.Serialize(new
        {
            type = "event",
            topic = evt.Topic,
            data = new
            {
                type = evt.Type,
                properties = evt.Payload
            }
        });
    }

    [Fact]
    public void Envelope_has_type_event_not_the_event_type()
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "hello" });
        var evt = new BroadcastEvent("session:abc", "message.updated", payload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("event", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Envelope_data_type_equals_original_event_type()
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "hello" });
        var evt = new BroadcastEvent("session:abc", "message.updated", payload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("message.updated", doc.RootElement
            .GetProperty("data")
            .GetProperty("type")
            .GetString());
    }

    [Fact]
    public void Envelope_topic_equals_broadcast_topic()
    {
        var payload = JsonSerializer.SerializeToElement(new { id = "s1" });
        var evt = new BroadcastEvent("session:fleet-123", "session.status", payload, DateTimeOffset.UtcNow);

        var json = SerializeEnvelope(evt);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("session:fleet-123", doc.RootElement.GetProperty("topic").GetString());
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
        Assert.Equal(JsonValueKind.Object, properties.ValueKind);
        Assert.Equal("hello world", properties.GetProperty("text").GetString());
        Assert.Equal(42, properties.GetProperty("count").GetInt32());
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
        Assert.False(root.TryGetProperty("payload", out _),
            "Top-level 'payload' field must not be present (use data.properties instead)");
        Assert.False(root.TryGetProperty("timestamp", out _),
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
        Assert.Equal(JsonValueKind.Object, properties.ValueKind);
        Assert.Empty(properties.EnumerateObject());
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
        Assert.Equal("event", root.GetProperty("type").GetString());
        Assert.Equal("session:fleet-abc", root.GetProperty("topic").GetString());

        // data object
        var data = root.GetProperty("data");
        Assert.Equal("message.updated", data.GetProperty("type").GetString());
        var props = data.GetProperty("properties");
        Assert.Equal("msg-1", props.GetProperty("messageId").GetString());
        Assert.Equal("assistant", props.GetProperty("role").GetString());
    }
}
