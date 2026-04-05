using System.Text.Json;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// Contract tests verifying that Claude Code NDJSON assistant message payloads, when mapped
/// through <see cref="ClaudeCodeMapper"/> and serialized with System.Text.Json (API settings),
/// produce the exact JSON shape defined in the shared contract fixtures.
/// </summary>
public sealed class ClaudeCodeToFleetContractTests
{
    /// <summary>
    /// Serialization options matching what ASP.NET Core minimal APIs use by default.
    /// Default camelCase naming, no custom converters.
    /// </summary>
    private static readonly JsonSerializerOptions ApiSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Options for deserializing Claude Code input payloads from fixtures.
    /// Uses the same options as the real Claude Code NDJSON client.
    /// </summary>
    private static readonly JsonSerializerOptions ClaudeCodeOptions = ClaudeCodeJsonOptions.Default;

    private static JsonDocument LoadFixture(string filename)
    {
        // Fixtures are copied to output directory via .csproj <Content> items
        var binDir = AppContext.BaseDirectory;
        var path = Path.Combine(binDir, "contracts", filename);
        var json = File.ReadAllText(path);
        return JsonDocument.Parse(json);
    }

    [Fact]
    public void All_Message_Cases_Match_Expected_Fleet_Shape()
    {
        using var doc = LoadFixture("claudecode-to-fleet-messages.json");
        var cases = doc.RootElement.GetProperty("cases");

        foreach (var testCase in cases.EnumerateArray())
        {
            var name = testCase.GetProperty("name").GetString();
            var claudeCodeInput = testCase.GetProperty("claudecode_input").GetRawText();
            var expectedJson = testCase.GetProperty("expected_fleet_message").GetRawText();

            // Deserialize Claude Code payload as stream message, then cast to assistant message
            var streamMessage = JsonSerializer.Deserialize<ClaudeCodeStreamMessage>(
                claudeCodeInput, ClaudeCodeOptions)!;

            Assert.IsType<ClaudeCodeAssistantMessage>(streamMessage);
            var assistantMessage = (ClaudeCodeAssistantMessage)streamMessage;

            // Map through ClaudeCodeMapper
            var harnessMessage = ClaudeCodeMapper.ToHarnessMessage(
                assistantMessage, DateTimeOffset.UnixEpoch);

            // Serialize with API settings (same as minimal API)
            var actualJson = JsonSerializer.Serialize(harnessMessage, ApiSerializerOptions);

            // Parse both for structural comparison
            using var expectedDoc = JsonDocument.Parse(expectedJson);
            using var actualDoc = JsonDocument.Parse(actualJson);

            AssertJsonEqual(expectedDoc.RootElement, actualDoc.RootElement,
                $"Contract mismatch for case '{name}'");
        }
    }

    /// <summary>
    /// Deep-compare two JsonElements, ignoring property order.
    /// Asserts structural equality for each expected property in the actual.
    /// </summary>
    private static void AssertJsonEqual(JsonElement expected, JsonElement actual, string context)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProps = new Dictionary<string, JsonElement>();
                foreach (var prop in expected.EnumerateObject())
                    expectedProps[prop.Name] = prop.Value;

                var actualProps = new Dictionary<string, JsonElement>();
                foreach (var prop in actual.EnumerateObject())
                    actualProps[prop.Name] = prop.Value;

                // Check that expected props exist in actual
                foreach (var (key, expectedVal) in expectedProps)
                {
                    Assert.True(actualProps.ContainsKey(key),
                        $"{context}: missing property '{key}' in actual. Actual keys: [{string.Join(", ", actualProps.Keys)}]");
                    AssertJsonEqual(expectedVal, actualProps[key], $"{context}.{key}");
                }
                break;

            case JsonValueKind.Array:
                var expectedArr = expected.EnumerateArray().ToList();
                var actualArr = actual.EnumerateArray().ToList();
                Assert.Equal(expectedArr.Count, actualArr.Count);
                for (var i = 0; i < expectedArr.Count; i++)
                    AssertJsonEqual(expectedArr[i], actualArr[i], $"{context}[{i}]");
                break;

            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;

            case JsonValueKind.Number:
                Assert.Equal(expected.GetRawText(), actual.GetRawText());
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), actual.GetBoolean());
                break;
        }
    }
}
