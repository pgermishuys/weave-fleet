using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// Real Claude Code stream-json lines (<c>tests/contracts/claudecode-work-events.json</c>) through
/// <see cref="ClaudeCodeTasks"/> give exactly the running work the contract says: each <c>work.*</c> event, the
/// conversation it belongs to, and the report it carries.
/// </summary>
public sealed class ClaudeCodeWorkContractTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var testCase in Load().RootElement.GetProperty("cases").EnumerateArray())
            data.Add(testCase.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Recorded_lines_report_the_running_work_in_the_contract(string name)
    {
        using var doc = Load();
        var testCase = doc.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var tasks = new ClaudeCodeTasks();
        var actual = new JsonArray();

        foreach (var line in testCase.GetProperty("lines").EnumerateArray())
        {
            var message = JsonSerializer.Deserialize(line.GetRawText(), ClaudeCodeJsonContext.Default.ClaudeCodeStreamMessage)!;
            switch (message)
            {
                case ClaudeCodeAssistantMessage assistant:
                    foreach (var toolUse in assistant.Message?.Content?.OfType<ClaudeCodeToolUseBlock>() ?? [])
                        tasks.ObserveToolUse(toolUse.Id!, toolUse.Name, toolUse.Input, assistant.ParentToolUseId);
                    break;
                case ClaudeCodeUserMessage user:
                    foreach (var result in user.Message?.Content?.OfType<ClaudeCodeToolResultBlock>() ?? [])
                        tasks.ObserveToolResult(result.Content);
                    break;
                case ClaudeCodeSystemMessage system:
                    foreach (var change in tasks.Observe(system))
                    {
                        actual.Add(new JsonObject
                        {
                            ["type"] = change.Type,
                            ["owner"] = change.OwnerCallId,
                            ["report"] = JsonNode.Parse(JsonSerializer.Serialize(change.Report, InfrastructureJsonContext.Default.WorkReport)),
                        });
                    }

                    break;
            }
        }

        var expected = JsonNode.Parse(testCase.GetProperty("expected_work").GetRawText())!;
        JsonNode.DeepEquals(expected, actual).ShouldBeTrue(
            $"Case '{name}'.\nExpected:\n{expected.ToJsonString(Indented)}\nActual:\n{actual.ToJsonString(Indented)}");
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonDocument Load()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "claudecode-work-events.json")));
}
