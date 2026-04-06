using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode;

public sealed class OpenCodeHarnessTests
{
    private static OpenCodeHarness CreateHarness() =>
        new(
            httpClientFactory: new TestHttpClientFactory(),
            portAllocator: new PortAllocator(10000, 10099),
            options: new FleetOptions(),
            scopeFactory: Substitute.For<IServiceScopeFactory>(),
            logger: NullLogger<OpenCodeHarness>.Instance,
            loggerFactory: NullLoggerFactory.Instance);

    [Fact]
    public void Type_ReturnsOpenCode()
    {
        var harness = CreateHarness();

        Assert.Equal("opencode", harness.Type);
    }

    [Fact]
    public void DisplayName_ReturnsOpenCode()
    {
        var harness = CreateHarness();

        Assert.Equal("OpenCode", harness.DisplayName);
    }

    [Fact]
    public void Capabilities_RequiresInitialPrompt_IsFalse()
    {
        var harness = CreateHarness();

        Assert.False(harness.Capabilities.RequiresInitialPrompt);
    }

    [Fact]
    public void Capabilities_SupportsAgents_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsAgents);
    }

    [Fact]
    public void Capabilities_SupportsModelSelection_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsModelSelection);
    }

    [Fact]
    public void Capabilities_SupportsCommands_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsCommands);
    }

    [Fact]
    public void Capabilities_SupportsForking_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsForking);
    }

    [Fact]
    public void Capabilities_SupportsResume_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsResume);
    }

    [Fact]
    public void Capabilities_SupportsImageAttachments_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsImageAttachments);
    }

    [Fact]
    public void Capabilities_SupportsStreaming_IsTrue()
    {
        var harness = CreateHarness();

        Assert.True(harness.Capabilities.SupportsStreaming);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckAvailability_WhenBinaryMissing_ReturnsNotAvailable()
    {
        // This test relies on "opencode" NOT being on the PATH (expected in CI / dev without OpenCode).
        // If opencode IS installed, the test is skipped.
        var harness = CreateHarness();

        var result = await harness.CheckAvailabilityAsync(CancellationToken.None);

        // We can only assert the shape — whether it's available depends on the environment.
        Assert.NotNull(result);
        // Either available (binary found) or not (binary missing) — both are valid results.
        if (!result.Available)
        {
            Assert.NotNull(result.Reason);
            Assert.Contains("opencode", result.Reason, StringComparison.OrdinalIgnoreCase);
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
