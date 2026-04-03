using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Domain.Tests.Harnesses;

public sealed class HarnessTypesTests
{
    [Fact]
    public void HarnessCapabilities_DefaultsToAllFalse()
    {
        var caps = new HarnessCapabilities();
        Assert.False(caps.RequiresInitialPrompt);
        Assert.False(caps.SupportsAgents);
        Assert.False(caps.SupportsModelSelection);
        Assert.False(caps.SupportsCommands);
        Assert.False(caps.SupportsForking);
        Assert.False(caps.SupportsResume);
        Assert.False(caps.SupportsImageAttachments);
        Assert.False(caps.SupportsStreaming);
    }

    [Fact]
    public void HarnessCapabilities_WithInitReturnsNewInstance()
    {
        var caps = new HarnessCapabilities { SupportsStreaming = true, RequiresInitialPrompt = true };
        Assert.True(caps.SupportsStreaming);
        Assert.True(caps.RequiresInitialPrompt);
        Assert.False(caps.SupportsAgents);
    }

    [Fact]
    public void HarnessAvailability_RecordEquality()
    {
        var a = new HarnessAvailability(true, null);
        var b = new HarnessAvailability(true, null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void HarnessInstanceStatus_HasExpectedValues()
    {
        var values = Enum.GetValues<HarnessInstanceStatus>();
        Assert.Equal(6, values.Length);
        Assert.Contains(HarnessInstanceStatus.Starting, values);
        Assert.Contains(HarnessInstanceStatus.Error, values);
    }

    [Fact]
    public void HealthCheckResult_RecordEquality()
    {
        var a = new HealthCheckResult(true, null);
        var b = new HealthCheckResult(true, null);
        Assert.Equal(a, b);
    }

    [Fact]
    public void HarnessMessage_RequiresAllProperties()
    {
        var msg = new HarnessMessage
        {
            Id = "msg-1",
            Role = "assistant",
            Content = "Hello",
            Timestamp = DateTimeOffset.UtcNow
        };
        Assert.Equal("assistant", msg.Role);
    }
}
