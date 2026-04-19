using System.Text.Json;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class ClientPayloadSanitizerTests
{
    [Fact]
    public void SanitizeMessages_RemovesReasoningPartsFromMixedMessages()
    {
        var messages = new[]
        {
            new HarnessMessage
            {
                Id = "msg-1",
                Role = "assistant",
                Parts =
                [
                    new TextPart("Visible"),
                    new ReasoningPart("Hidden")
                ],
                Timestamp = DateTimeOffset.UtcNow,
            }
        };

        var sanitized = ClientPayloadSanitizer.SanitizeMessages(messages);

        sanitized.Count.ShouldBe(1);
        sanitized[0].Parts.Count.ShouldBe(1);
        sanitized[0].Parts[0].ShouldBeOfType<TextPart>().Text.ShouldBe("Visible");
    }

    [Fact]
    public void SanitizeMessages_PreservesMessagesThatOnlyContainReasoningAsEmpty()
    {
        var messages = new[]
        {
            new HarnessMessage
            {
                Id = "msg-1",
                Role = "assistant",
                Parts = [new ReasoningPart("Hidden")],
                Timestamp = DateTimeOffset.UtcNow,
            },
            new HarnessMessage
            {
                Id = "msg-2",
                Role = "assistant",
                Parts = [new TextPart("Visible")],
                Timestamp = DateTimeOffset.UtcNow,
            }
        };

        var sanitized = ClientPayloadSanitizer.SanitizeMessages(messages);

        sanitized.Count.ShouldBe(2);
        sanitized[0].Id.ShouldBe("msg-1");
        sanitized[0].Parts.ShouldBeEmpty();
        sanitized[1].Id.ShouldBe("msg-2");
    }

    [Fact]
    public void SanitizeEventPayload_DropsReasoningPartUpdates()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            sessionID = "sess-1",
            part = new
            {
                id = "part-1",
                messageID = "msg-1",
                type = "reasoning",
                text = "Hidden",
            }
        });

        var sanitized = ClientPayloadSanitizer.SanitizeEventPayload("message.part.updated", payload);

        sanitized.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void SanitizeEventPayload_PreservesNonReasoningPartUpdates()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            sessionID = "sess-1",
            part = new
            {
                id = "part-1",
                messageID = "msg-1",
                type = "text",
                text = "Visible",
            }
        });

        var sanitized = ClientPayloadSanitizer.SanitizeEventPayload("message.part.updated", payload);

        sanitized.HasValue.ShouldBeTrue();
        sanitized.Value.GetProperty("part").GetProperty("type").GetString().ShouldBe("text");
    }

    [Fact]
    public void SanitizeEventPayload_RemovesReasoningFromMessageUpdatedPayload()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg-1",
                role = "assistant",
            },
            parts = new object[]
            {
                new { type = "text", text = "Visible" },
                new { type = "reasoning", text = "Hidden" }
            }
        });

        var sanitized = ClientPayloadSanitizer.SanitizeEventPayload("message.updated", payload);

        sanitized.HasValue.ShouldBeTrue();
        var parts = sanitized.Value.GetProperty("parts");
        parts.GetArrayLength().ShouldBe(1);
        parts[0].GetProperty("type").GetString().ShouldBe("text");
    }

    [Fact]
    public void SanitizeEventPayload_PreservesCommittedSnapshotTextWhileDroppingReasoning()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg-1",
                role = "assistant",
            },
            parts = new object[]
            {
                new { id = "part-1", type = "text", text = "Merged final text" },
                new { id = "part-r1", type = "reasoning", text = "Hidden" }
            }
        });

        var sanitized = ClientPayloadSanitizer.SanitizeEventPayload("message.updated", payload);

        sanitized.HasValue.ShouldBeTrue();
        var parts = sanitized.Value.GetProperty("parts");
        parts.GetArrayLength().ShouldBe(1);
        parts[0].GetProperty("text").GetString().ShouldBe("Merged final text");
    }

    [Fact]
    public void SanitizeEventPayload_PreservesReasoningOnlyAssistantMessageUpdatedPayloadAsEmpty()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = "msg-1",
                role = "assistant",
            },
            parts = new object[]
            {
                new { type = "reasoning", text = "Hidden" }
            }
        });

        var sanitized = ClientPayloadSanitizer.SanitizeEventPayload("message.updated", payload);

        sanitized.HasValue.ShouldBeTrue();
        sanitized.Value.GetProperty("parts").GetArrayLength().ShouldBe(0);
    }
}
