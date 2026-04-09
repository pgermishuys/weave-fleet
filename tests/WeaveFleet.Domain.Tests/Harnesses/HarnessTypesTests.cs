using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Domain.Tests.Harnesses;

public sealed class HarnessTypesTests
{
    [Fact]
    public void HarnessCapabilities_DefaultsToAllFalse()
    {
        var caps = new HarnessCapabilities();
        caps.RequiresInitialPrompt.ShouldBeFalse();
        caps.SupportsAgents.ShouldBeFalse();
        caps.SupportsModelSelection.ShouldBeFalse();
        caps.SupportsCommands.ShouldBeFalse();
        caps.SupportsForking.ShouldBeFalse();
        caps.SupportsResume.ShouldBeFalse();
        caps.SupportsImageAttachments.ShouldBeFalse();
        caps.SupportsStreaming.ShouldBeFalse();
    }

    [Fact]
    public void HarnessCapabilities_WithInitReturnsNewInstance()
    {
        var caps = new HarnessCapabilities { SupportsStreaming = true, RequiresInitialPrompt = true };
        caps.SupportsStreaming.ShouldBeTrue();
        caps.RequiresInitialPrompt.ShouldBeTrue();
        caps.SupportsAgents.ShouldBeFalse();
    }

    [Fact]
    public void HarnessAvailability_RecordEquality()
    {
        var a = new HarnessAvailability(true, null);
        var b = new HarnessAvailability(true, null);
        a.ShouldBe(b);
    }

    [Fact]
    public void HarnessInstanceStatus_HasExpectedValues()
    {
        var values = Enum.GetValues<HarnessInstanceStatus>();
        values.Length.ShouldBe(6);
        values.ShouldContain(HarnessInstanceStatus.Starting);
        values.ShouldContain(HarnessInstanceStatus.Error);
    }

    [Fact]
    public void HealthCheckResult_RecordEquality()
    {
        var a = new HealthCheckResult(true, null);
        var b = new HealthCheckResult(true, null);
        a.ShouldBe(b);
    }

    [Fact]
    public void HarnessMessage_RequiresAllProperties()
    {
        var msg = new HarnessMessage
        {
            Id = "msg-1",
            Role = "assistant",
            Parts = [new TextPart("Hello")],
            Timestamp = DateTimeOffset.UtcNow
        };
        msg.Role.ShouldBe("assistant");
        msg.TextContent.ShouldBe("Hello");
    }
}
