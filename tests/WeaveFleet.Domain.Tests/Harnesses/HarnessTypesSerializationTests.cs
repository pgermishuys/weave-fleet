using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Domain.Tests.Harnesses;

public sealed class HarnessTypesSerializationTests
{
    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void TextPart_Serializes_With_Type_Discriminator_And_Text()
    {
        MessagePart part = new TextPart("Hello world");
        // Use camelCase options to match ASP.NET Core API serialization
        var json = JsonSerializer.Serialize(part, CamelCaseOptions);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("text");
        doc.RootElement.GetProperty("text").GetString().ShouldBe("Hello world");
    }

    [Fact]
    public void ToolUsePart_Serializes_With_All_Fields()
    {
        MessagePart part = new ToolUsePart("call-1", "bash", JsonSerializer.SerializeToElement(new { cmd = "ls" }), ToolUseState.Running);
        var json = JsonSerializer.Serialize(part, CamelCaseOptions);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("tool");
        doc.RootElement.GetProperty("toolCallId").GetString().ShouldBe("call-1");
        doc.RootElement.GetProperty("toolName").GetString().ShouldBe("bash");
    }

    [Fact]
    public void ReasoningPart_Serializes_With_Type_Discriminator_And_Summary()
    {
        MessagePart part = new ReasoningPart("Thinking", "short summary");
        var json = JsonSerializer.Serialize(part, CamelCaseOptions);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("reasoning");
        doc.RootElement.GetProperty("text").GetString().ShouldBe("Thinking");
        doc.RootElement.GetProperty("summary").GetString().ShouldBe("short summary");
    }

    [Fact]
    public void MessagePage_Serializes_With_Parts_Fully_Populated()
    {
        var page = new MessagePage(
            new[] {
                new HarnessMessage {
                    Id = "msg-1",
                    Role = "assistant",
                    Parts = new MessagePart[] { new TextPart("Hi") },
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000),
                }
            },
            false);
        var json = JsonSerializer.Serialize(page, CamelCaseOptions);
        using var doc = JsonDocument.Parse(json);
        var firstPart = doc.RootElement.GetProperty("messages")[0].GetProperty("parts")[0];
        firstPart.GetProperty("type").GetString().ShouldBe("text");
        firstPart.GetProperty("text").GetString().ShouldBe("Hi");
    }
}
