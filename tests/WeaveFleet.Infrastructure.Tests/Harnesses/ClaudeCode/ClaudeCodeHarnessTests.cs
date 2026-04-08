using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

public sealed class ClaudeCodeHarnessTests
{
    private static ClaudeCodeHarness CreateHarness() =>
        new(
            options: new FleetOptions(),
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            logger: NullLogger<ClaudeCodeHarness>.Instance,
            loggerFactory: NullLoggerFactory.Instance);

    [Fact]
    public void Type_ReturnsClaudeCode()
    {
        var harness = CreateHarness();

        Assert.Equal("claude-code", harness.Type);
    }

    [Fact]
    public void DisplayName_ReturnsClaudeCode()
    {
        var harness = CreateHarness();

        Assert.Equal("Claude Code", harness.DisplayName);
    }

    [Fact]
    public void Capabilities_RequiresInitialPrompt_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.RequiresInitialPrompt);
    }

    [Fact]
    public void Capabilities_SupportsResume_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsResume);
    }

    [Fact]
    public void Capabilities_SupportsModelSelection_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsModelSelection);
    }

    [Fact]
    public void Capabilities_SupportsStreaming_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsStreaming);
    }

    [Fact]
    public void Capabilities_SupportsAgents_IsFalse()
    {
        var harness = CreateHarness();

        Assert.False(harness.Capabilities.SupportsAgents);
    }

    [Fact]
    public void Capabilities_SupportsCommands_IsFalse()
    {
        var harness = CreateHarness();

        Assert.False(harness.Capabilities.SupportsCommands);
    }

    [Fact]
    public void Capabilities_SupportsForking_IsFalse()
    {
        var harness = CreateHarness();

        Assert.False(harness.Capabilities.SupportsForking);
    }

    [Fact]
    public void Capabilities_SupportsImageAttachments_IsFalse()
    {
        var harness = CreateHarness();

        Assert.False(harness.Capabilities.SupportsImageAttachments);
    }

    [Fact]
    public void Capabilities_SupportsDelegation_IsFalse()
    {
        var harness = CreateHarness();

        Assert.False(harness.Capabilities.SupportsDelegation);
    }

    [Fact(Skip = "Integration: requires claude binary on PATH")]
    public async Task CheckAvailability_WhenBinaryMissing_ReturnsNotAvailable()
    {
        // Use a non-existent binary path to simulate missing claude
        var options = new FleetOptions();
        options.ClaudeCode.BinaryPath = "/nonexistent/path/to/claude-definitely-not-here";
        var harness = new ClaudeCodeHarness(
            options: options,
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            logger: NullLogger<ClaudeCodeHarness>.Instance,
            loggerFactory: NullLoggerFactory.Instance);

        var result = await harness.CheckAvailabilityAsync(CancellationToken.None);

        Assert.False(result.Available);
        Assert.NotNull(result.Reason);
    }
}
