using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness;

/// <summary>
/// Loads contract fixture JSON files embedded in this assembly and converts them
/// into <see cref="HarnessMessage"/> objects for use in test scenarios.
/// </summary>
public static class FixtureLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Load messages from the <c>fleet-api-messages.json</c> contract fixture.
    /// Returns the list of <see cref="HarnessMessage"/> objects in the contract.
    /// </summary>
    public static IReadOnlyList<HarnessMessage> LoadFleetApiMessages()
        => LoadMessagesFromFixture("fleet-api-messages.json");

    /// <summary>
    /// Load expected Fleet messages from the <c>opencode-to-fleet-messages.json</c> contract fixture.
    /// Each case's <c>expected_fleet_message</c> is deserialized into a <see cref="HarnessMessage"/>.
    /// </summary>
    public static IReadOnlyList<HarnessMessage> LoadOpenCodeMessages()
        => LoadFromOpenCodeFixture();

    /// <summary>
    /// Load the raw JSON text of a named fixture file (e.g. "fleet-api-messages.json").
    /// </summary>
    public static string LoadRawJson(string fileName)
    {
        var resourceName = $"contracts/{fileName}";
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found in {assembly.FullName}. " +
                $"Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static IReadOnlyList<HarnessMessage> LoadMessagesFromFixture(string fileName)
    {
        var json = LoadRawJson(fileName);
        var doc = JsonSerializer.Deserialize<FleetApiMessagesFixture>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize fixture '{fileName}'.");
        return doc.Messages ?? [];
    }

    private static List<HarnessMessage> LoadFromOpenCodeFixture()
    {
        var json = LoadRawJson("opencode-to-fleet-messages.json");
        var doc = JsonSerializer.Deserialize<OpenCodeToFleetFixture>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize opencode-to-fleet-messages.json.");

        return doc.Cases?
            .Select(c => c.ExpectedFleetMessage)
            .OfType<HarnessMessage>()
            .ToList() ?? [];
    }

    // ── Private DTOs ─────────────────────────────────────────────────────────

    private sealed class FleetApiMessagesFixture
    {
        [JsonPropertyName("messages_response")]
        public MessagesResponse? MessagesResponse { get; init; }

        public IReadOnlyList<HarnessMessage>? Messages =>
            MessagesResponse?.Messages;
    }

    private sealed class MessagesResponse
    {
        [JsonPropertyName("messages")]
        public IReadOnlyList<HarnessMessage>? Messages { get; init; }
    }

    private sealed class OpenCodeToFleetFixture
    {
        [JsonPropertyName("cases")]
        public IReadOnlyList<OpenCodeCase>? Cases { get; init; }
    }

    private sealed class OpenCodeCase
    {
        [JsonPropertyName("expected_fleet_message")]
        public HarnessMessage? ExpectedFleetMessage { get; init; }
    }
}
