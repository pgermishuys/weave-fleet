using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Harnesses;
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
    public void Capabilities_RequiresInitialPrompt_IsFalse()
    {
        var harness = CreateHarness();

        harness.Capabilities.RequiresInitialPrompt.ShouldBeFalse();
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

    [Fact]
    public async Task CheckAvailability_WhenBinaryMissing_ReturnsNotInstalled()
    {
        // A configured path with no file there: nothing is started.
        var runtime = CreateRuntimeWithBinary("/nonexistent/path/to/claude-definitely-not-here");

        var result = await runtime.CheckAvailabilityAsync(CancellationToken.None);

        result.Available.ShouldBeFalse();
        result.State.ShouldBe(HarnessStates.NotInstalled);
        result.Reason.ShouldBe("Claude Code isn't installed: there's no file at /nonexistent/path/to/claude-definitely-not-here.");
    }

    [Fact]
    public void GetSetup_OffersTheNativeInstaller_AndSignsInWithTheExecutableFleetFound()
    {
        if (OperatingSystem.IsWindows()) return;
        var runtime = CreateRuntimeWithBinary("claude");

        var setup = runtime.GetSetup(HarnessAvailability.SignInRequired("Sign in.", "2.1.276", "/Users/Jo Smith/.local/bin/claude"));

        setup.InstallCommand.ShouldBe("curl -fsSL https://claude.ai/install.sh | bash");
        setup.SignInCommand.ShouldBe("'/Users/Jo Smith/.local/bin/claude' auth login");
        setup.DocsUrl.ShouldBe("https://code.claude.com/docs/en/setup");
        runtime.GetSetup(HarnessAvailability.NotInstalled("Missing.")).SignInCommand.ShouldBe("claude auth login");
    }

    private static ClaudeCodeHarnessRuntime CreateRuntimeWithBinary(string binaryPath)
    {
        var options = new FleetOptions();
        options.ClaudeCode.BinaryPath = binaryPath;
        return new ClaudeCodeHarnessRuntime(
            options: options,
            scopeFactory: TestServiceScopeFactory.CreateEmpty(),
            logger: NullLogger<ClaudeCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance);
    }
}
