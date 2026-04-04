using System.Text.Json;

namespace WeaveFleet.Infrastructure.Tests.Services;

/// <summary>
/// Contract tests verifying that OpenCode SSE event payloads, after session ID
/// rewriting, match the expected Fleet event payload shape.
///
/// Since RewriteSessionIds is a private method on HarnessEventRelay, these tests
/// mirror the same algorithm. This contract test verifies the fixture expectations
/// independently of the production implementation.
/// </summary>
public sealed class EventRewriteContractTests
{
    private static JsonDocument LoadFixture(string filename)
    {
        // Fixtures are copied to output directory via .csproj <Content> items
        var binDir = AppContext.BaseDirectory;
        var path = Path.Combine(binDir, "contracts", filename);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Mirrors the RewriteSessionIds logic from HarnessEventRelay.
    /// Recursively replaces sessionId/sessionID string values.
    /// </summary>
    private static JsonElement RewriteSessionIds(JsonElement source, string fleetSessionId)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return source;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteRewritten(writer, source, fleetSessionId);
        }
        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    private static void WriteRewritten(Utf8JsonWriter writer, JsonElement element, string fleetSessionId)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    if ((prop.Name == "sessionId" || prop.Name == "sessionID")
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        writer.WriteStringValue(fleetSessionId);
                    }
                    else
                    {
                        WriteRewritten(writer, prop.Value, fleetSessionId);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteRewritten(writer, item, fleetSessionId);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    [Fact]
    public void All_Event_Cases_Match_Expected_Fleet_Payload()
    {
        using var doc = LoadFixture("opencode-to-fleet-events.json");
        var cases = doc.RootElement.GetProperty("cases");

        foreach (var testCase in cases.EnumerateArray())
        {
            var name = testCase.GetProperty("name").GetString();
            var fleetSessionId = testCase.GetProperty("fleet_session_id").GetString()!;
            var openCodePayload = testCase.GetProperty("opencode_event")
                .GetProperty("properties");
            var expectedPayloadJson = testCase.GetProperty("expected_fleet_event_payload").GetRawText();

            // Apply session ID rewriting
            var rewritten = RewriteSessionIds(openCodePayload, fleetSessionId);
            var actualJson = JsonSerializer.Serialize(rewritten);

            using var expectedDoc = JsonDocument.Parse(expectedPayloadJson);
            using var actualDoc = JsonDocument.Parse(actualJson);

            // Deep structural comparison
            Assert.Equal(
                JsonSerializer.Serialize(expectedDoc.RootElement),
                JsonSerializer.Serialize(actualDoc.RootElement));
        }
    }
}
