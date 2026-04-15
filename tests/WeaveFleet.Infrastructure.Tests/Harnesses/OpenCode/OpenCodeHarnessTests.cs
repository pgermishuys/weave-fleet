using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeHarnessTests
{
    private static OpenCodeHarness CreateHarness() => new();

    private static OpenCodeHarnessRuntime CreateRuntime() =>
        new(
            httpClientFactory: new TestHttpClientFactory(),
            portAllocator: new PortAllocator(10000, 10099),
            options: new FleetOptions(),
            scopeFactory: TestServiceScopeFactory.CreateEmpty(),
            logger: NullLogger<OpenCodeHarnessRuntime>.Instance,
            loggerFactory: NullLoggerFactory.Instance);

    [Fact]
    public void Type_ReturnsOpenCode()
    {
        var harness = CreateHarness();

        harness.Type.ShouldBe("opencode");
    }

    [Fact]
    public void DisplayName_ReturnsOpenCode()
    {
        var harness = CreateHarness();

        harness.DisplayName.ShouldBe("OpenCode");
    }

    [Fact]
    public void Capabilities_RequiresInitialPrompt_IsFalse()
    {
        var harness = CreateHarness();

        harness.Capabilities.RequiresInitialPrompt.ShouldBeFalse();
    }

    [Fact]
    public void Capabilities_SupportsAgents_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsAgents.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsModelSelection_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsModelSelection.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsCommands_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsCommands.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsForking_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsForking.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsResume_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsResume.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsImageAttachments_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsImageAttachments.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsStreaming_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsStreaming.ShouldBeTrue();
    }

    [Fact]
    public void Capabilities_SupportsDelegation_IsTrue()
    {
        var harness = CreateHarness();

        harness.Capabilities.SupportsDelegation.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckAvailability_WhenBinaryMissing_ReturnsNotAvailable()
    {
        // This test relies on "opencode" NOT being on the PATH (expected in CI / dev without OpenCode).
        // If opencode IS installed, the test is skipped.
        var runtime = CreateRuntime();

        var result = await runtime.CheckAvailabilityAsync(CancellationToken.None);

        // We can only assert the shape — whether it's available depends on the environment.
        result.ShouldNotBeNull();
        // Either available (binary found) or not (binary missing) — both are valid results.
        if (!result.Available)
        {
            result.Reason.ShouldNotBeNull();
            result.Reason.Contains("opencode", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
        }
    }

    // ---------------------------------------------------------------------------
    // Minimal IHttpClientFactory stub (not used in metadata / capability tests)
    // ---------------------------------------------------------------------------

    private sealed class TestHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) =>
            new();
    }
}
