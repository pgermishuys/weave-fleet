using System.Text.Json;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Tests.Services;

public sealed class ReasoningFilterTests
{
    // -----------------------------------------------------------------------
    // FilterMessageEventPayload
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterMessageEventPayload_NonObjectPayload_ReturnsClone()
    {
        var payload = JsonSerializer.SerializeToElement("not-an-object");
        var result = ReasoningFilter.FilterMessageEventPayload(payload);
        result.ShouldNotBeNull();
        result!.Value.GetString().ShouldBe("not-an-object");
    }

    [Fact]
    public void FilterMessageEventPayload_NoPartsProperty_ReturnsClone()
    {
        var payload = JsonSerializer.SerializeToElement(new { info = new { role = "assistant" } });
        var result = ReasoningFilter.FilterMessageEventPayload(payload);
        result.ShouldNotBeNull();
        result!.Value.TryGetProperty("info", out _).ShouldBeTrue();
    }

    [Fact]
    public void FilterMessageEventPayload_NoReasoningParts_ReturnsCloneUnchanged()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            info = new { role = "assistant" },
            parts = new[] { new { type = "text", text = "hello" } }
        });

        var result = ReasoningFilter.FilterMessageEventPayload(payload);
        result.ShouldNotBeNull();
        result!.Value.GetProperty("parts").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public void FilterMessageEventPayload_MixedParts_StripsReasoningParts()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            info = new { role = "assistant" },
            parts = new object[]
            {
                new { type = "reasoning", text = "thinking..." },
                new { type = "text", text = "answer" }
            }
        });

        var result = ReasoningFilter.FilterMessageEventPayload(payload);
        result.ShouldNotBeNull();
        var parts = result!.Value.GetProperty("parts");
        parts.GetArrayLength().ShouldBe(1);
        parts[0].GetProperty("type").GetString().ShouldBe("text");
    }

    [Fact]
    public void FilterMessageEventPayload_AssistantMessageWithOnlyReasoningParts_ReturnsEmptyParts()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            info = new { role = "assistant" },
            parts = new[] { new { type = "reasoning", text = "thinking..." } }
        });

        var result = ReasoningFilter.FilterMessageEventPayload(payload);
        result.ShouldNotBeNull();
        result!.Value.GetProperty("parts").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void FilterMessageEventPayload_UserMessageWithOnlyReasoningParts_ReturnsEmptyParts()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            info = new { role = "user" },
            parts = new[] { new { type = "reasoning", text = "thinking..." } }
        });

        var result = ReasoningFilter.FilterMessageEventPayload(payload);
        result.ShouldNotBeNull();
        result!.Value.GetProperty("parts").GetArrayLength().ShouldBe(0);
    }

    // -----------------------------------------------------------------------
    // IsReasoningPartEvent
    // -----------------------------------------------------------------------

    [Fact]
    public void IsReasoningPartEvent_NonObjectPayload_ReturnsFalse()
    {
        var payload = JsonSerializer.SerializeToElement("not-an-object");
        ReasoningFilter.IsReasoningPartEvent(payload).ShouldBeFalse();
    }

    [Fact]
    public void IsReasoningPartEvent_NoPart_ReturnsFalse()
    {
        var payload = JsonSerializer.SerializeToElement(new { other = "value" });
        ReasoningFilter.IsReasoningPartEvent(payload).ShouldBeFalse();
    }

    [Fact]
    public void IsReasoningPartEvent_TextPart_ReturnsFalse()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            part = new { type = "text", text = "hello" }
        });
        ReasoningFilter.IsReasoningPartEvent(payload).ShouldBeFalse();
    }

    [Fact]
    public void IsReasoningPartEvent_ReasoningPart_ReturnsTrue()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            part = new { type = "reasoning", text = "thinking..." }
        });
        ReasoningFilter.IsReasoningPartEvent(payload).ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // FilterDurableParts
    // -----------------------------------------------------------------------

    [Fact]
    public void FilterDurableParts_EmptyList_ReturnsEmpty()
    {
        var result = ReasoningFilter.FilterDurableParts([]);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void FilterDurableParts_NoReasoningParts_ReturnsAllParts()
    {
        MessagePart[] parts = [new TextPart("hello"), new TextPart("world")];
        var result = ReasoningFilter.FilterDurableParts(parts);
        result.Length.ShouldBe(2);
    }

    [Fact]
    public void FilterDurableParts_MixedParts_StripsReasoningParts()
    {
        MessagePart[] parts =
        [
            new TextPart("hello"),
            new ReasoningPart("thinking..."),
            new TextPart("world"),
        ];
        var result = ReasoningFilter.FilterDurableParts(parts);
        result.Length.ShouldBe(2);
        result.ShouldAllBe(p => p is TextPart);
    }

    [Fact]
    public void FilterDurableParts_OnlyReasoningParts_ReturnsEmpty()
    {
        MessagePart[] parts = [new ReasoningPart("thinking...")];
        var result = ReasoningFilter.FilterDurableParts(parts);
        result.ShouldBeEmpty();
    }
}
