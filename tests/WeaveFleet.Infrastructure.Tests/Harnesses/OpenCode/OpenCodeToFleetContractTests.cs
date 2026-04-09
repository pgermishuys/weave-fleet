using System.Text.Json;
using Shouldly;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

/// <summary>
/// Contract tests verifying that OpenCode message payloads, when mapped through
/// OpenCodeMapper and serialized with System.Text.Json (API settings), produce
/// the exact JSON shape defined in the shared contract fixtures.
/// </summary>
public sealed class OpenCodeToFleetContractTests
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
    /// Options for deserializing OpenCode input payloads from fixtures.
    /// Uses the same options as the real OpenCode HTTP client.
    /// </summary>
    private static readonly JsonSerializerOptions OpenCodeOptions = OpenCodeJsonOptions.Default;

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
        using var doc = LoadFixture("opencode-to-fleet-messages.json");
        var cases = doc.RootElement.GetProperty("cases");

        foreach (var testCase in cases.EnumerateArray())
        {
            var name = testCase.GetProperty("name").GetString();
            var openCodeInput = testCase.GetProperty("opencode_input").GetRawText();
            var expectedJson = testCase.GetProperty("expected_fleet_message").GetRawText();

            // Deserialize OpenCode payload
            var msgWithParts = JsonSerializer.Deserialize<OpenCodeMessageWithParts>(
                openCodeInput, OpenCodeOptions)!;

            // Map through OpenCodeMapper
            var harnessMessage = OpenCodeMapper.ToHarnessMessage(msgWithParts);

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
        actual.ValueKind.ShouldBe(expected.ValueKind);

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
                    actualProps.ContainsKey(key).ShouldBeTrue(
                        $"{context}: missing property '{key}' in actual. Actual keys: [{string.Join(", ", actualProps.Keys)}]");
                    AssertJsonEqual(expectedVal, actualProps[key], $"{context}.{key}");
                }
                break;

            case JsonValueKind.Array:
                var expectedArr = expected.EnumerateArray().ToList();
                var actualArr = actual.EnumerateArray().ToList();
                actualArr.Count.ShouldBe(expectedArr.Count);
                for (int i = 0; i < expectedArr.Count; i++)
                    AssertJsonEqual(expectedArr[i], actualArr[i], $"{context}[{i}]");
                break;

            case JsonValueKind.String:
                actual.GetString().ShouldBe(expected.GetString());
                break;

            case JsonValueKind.Number:
                actual.GetRawText().ShouldBe(expected.GetRawText());
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                actual.GetBoolean().ShouldBe(expected.GetBoolean());
                break;
        }
    }
}
