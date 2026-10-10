using System.Text.Json;
using WeaveFleet.Application.Mods;

namespace WeaveFleet.Application.Tests.Mods;

/// <summary>The mod tools read <c>files</c> and <c>e</c> the same way from the plugins and from MCP, as JSON or as JSON text.</summary>
public sealed class ModBridgeArgumentsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Files_come_from_a_list()
        => ModBridge.ReadFiles(Json("""[{"path":"mod.json","content":"{}"},{"path":"mod.ts","content":"x"}]"""))
            .ShouldBe([new ModFile("mod.json", "{}"), new ModFile("mod.ts", "x")]);

    [Fact]
    public void Files_sent_as_JSON_text_are_read_as_the_list()
        => ModBridge.ReadFiles(Json("""{"files":"[{\"path\":\"mod.ts\",\"content\":\"x\"}]"}""").GetProperty("files"))
            .ShouldBe([new ModFile("mod.ts", "x")]);

    [Fact]
    public void An_entry_without_a_path_or_content_is_passed_with_an_empty_path_for_the_tool_to_refuse()
        => ModBridge.ReadFiles(Json("""[{"path":"mod.ts"},{"content":"x"},"mod.ts"]"""))
            .ShouldBe([new ModFile("", ""), new ModFile("", ""), new ModFile("", "")]);

    [Theory]
    [InlineData("""{"files":"not json"}""")]
    [InlineData("""{"files":{"path":"mod.ts"}}""")]
    [InlineData("""{"files":null}""")]
    public void Files_that_arent_a_list_are_none(string args)
        => ModBridge.ReadFiles(Json(args).GetProperty("files")).ShouldBeNull();

    [Fact]
    public void Missing_files_are_none() => ModBridge.ReadFiles(default).ShouldBeNull();

    [Fact]
    public void An_event_sent_as_JSON_text_is_read_as_the_object()
    {
        var e = ModBridge.ReadObject(Json("""{"e":"{\"component\":\"ToolUse\"}"}""").GetProperty("e"));

        e.ValueKind.ShouldBe(JsonValueKind.Object);
        e.GetProperty("component").GetString().ShouldBe("ToolUse");
    }

    [Fact]
    public void Text_that_isnt_JSON_stays_text_for_the_tool_to_refuse()
        => ModBridge.ReadObject(Json("""{"e":"ToolUse"}""").GetProperty("e")).ValueKind.ShouldBe(JsonValueKind.String);
}
