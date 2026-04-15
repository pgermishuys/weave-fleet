using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

public sealed class ClaudeCodeHarnessTests
{
    private static ClaudeCodeHarness CreateHarness() => new();

    [Fact]
    public void Type_ReturnsClaudeCode()
    {
        var harness = CreateHarness();

        harness.Type.ShouldBe("claude-code");
    }

    [Fact]
    public void DisplayName_ReturnsClaudeCode()
    {
        var harness = CreateHarness();

        harness.DisplayName.ShouldBe("Claude Code");
    }

    [Fact]
    public void Capabilities_RequiresInitialPrompt_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.RequiresInitialPrompt.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsResume_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsResume.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsModelSelection_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsModelSelection.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsStreaming_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsAgents_IsFalse()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsAgents.ShouldBeFalse();
    }

    [Fact]
    public void Capabilities_SupportsCommands_IsFalse()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsCommands.ShouldBeFalse();
    }

    [Fact]
    public void Capabilities_SupportsForking_IsFalse()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsForking.ShouldBeFalse();
    }

    [Fact]
    public void Capabilities_SupportsImageAttachments_IsFalse()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsImageAttachments.ShouldBeFalse();
    }

    [Fact]
    public void Capabilities_SupportsDelegation_IsFalse()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsDelegation.ShouldBeFalse();
    }

    [Fact(Skip = "Integration: requires claude binary on PATH")]
    public async Task CheckAvailability_WhenBinaryMissing_ReturnsNotAvailable()
    {
        // Use a non-existent binary path to simulate missing claude
        var options = new FleetOptions();
        options.ClaudeCode.BinaryPath = "/nonexistent/path/to/claude-definitely-not-here";
        var runtime = new ClaudeCodeHarnessRuntime(
            options: options,
            scopeFactory: TestServiceScopeFactory.CreateEmpty(),
            logger: NullLogger<ClaudeCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance);

        var result = await runtime.CheckAvailabilityAsync(CancellationToken.None);

        result.Available.ShouldBeFalse();
        result.Reason.ShouldNotBeNull();
    }
}
