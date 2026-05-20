using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Pi;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class HarnessRegistryTests
{
    [Fact]
    public void GetAll_WhenEmpty_ReturnsEmptyList()
    {
        var registry = new HarnessRegistry([], []);
        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public void GetByType_ReturnsMatchingHarness()
    {
        var harness = new FakeHarness("opencode", "OpenCode");
        var registry = new HarnessRegistry([harness], []);

        var found = registry.GetByType("opencode");
        found.ShouldNotBeNull();
        found.Type.ShouldBe("opencode");
    }

    [Fact]
    public void GetByType_IsCaseInsensitive()
    {
        var harness = new FakeHarness("opencode", "OpenCode");
        var registry = new HarnessRegistry([harness], []);

        registry.GetByType("OpenCode").ShouldNotBeNull();
        registry.GetByType("OPENCODE").ShouldNotBeNull();
    }

    [Fact]
    public void GetByType_ReturnsNullWhenNotFound()
    {
        var registry = new HarnessRegistry([], []);
        registry.GetByType("nonexistent").ShouldBeNull();
    }

    [Fact]
    public void GetRuntimeByType_ReturnsMatchingRuntime()
    {
        var runtime = new FakeHarnessRuntime("opencode");
        var registry = new HarnessRegistry([], [runtime]);

        var found = registry.GetRuntimeByType("opencode");
        found.ShouldNotBeNull();
        found.HarnessType.ShouldBe("opencode");
    }

    [Fact]
    public void GetRuntimeByType_IsCaseInsensitive()
    {
        var runtime = new FakeHarnessRuntime("opencode");
        var registry = new HarnessRegistry([], [runtime]);

        registry.GetRuntimeByType("OpenCode").ShouldNotBeNull();
        registry.GetRuntimeByType("OPENCODE").ShouldNotBeNull();
    }

    [Fact]
    public void GetRuntimeByType_ReturnsNullWhenNotFound()
    {
        var registry = new HarnessRegistry([], []);
        registry.GetRuntimeByType("nonexistent").ShouldBeNull();
    }

    [Fact]
    public async Task GetAvailabilityAsync_AggregatesAllHarnesses()
    {
        var h1 = new FakeHarness("opencode", "OpenCode");
        var h2 = new FakeHarness("claude-code", "Claude Code");
        var r1 = new FakeHarnessRuntime("opencode", available: true);
        var r2 = new FakeHarnessRuntime("claude-code", available: false, availabilityReason: "Binary not found");
        var registry = new HarnessRegistry([h1, h2], [r1, r2]);

        var results = await registry.GetAvailabilityAsync(CancellationToken.None);

        results.Count.ShouldBe(2);
        results[0].Type.ShouldBe("opencode");
        results[0].Available.ShouldBeTrue();
        results[1].DisplayName.ShouldBe("Claude Code");
        results[1].Available.ShouldBeFalse();
        results[1].Reason.ShouldBe("Binary not found");
    }

    [Fact]
    public async Task GetAvailabilityAsync_NoRuntime_ReturnsNotAvailable()
    {
        var harness = new FakeHarness("opencode", "OpenCode");
        var registry = new HarnessRegistry([harness], []);

        var results = await registry.GetAvailabilityAsync(CancellationToken.None);

        results.Count.ShouldBe(1);
        results[0].Available.ShouldBeFalse();
        results[0].Reason.ShouldBe("No runtime registered.");
    }

    [Fact]
    public void add_fleet_infrastructure_registers_pi_harness_and_runtime()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFleetInfrastructure(new FleetOptions { AnalyticsEnabled = false });

        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<IHarnessRegistry>();

        registry.GetByType("pi").ShouldBeOfType<PiHarness>();
        registry.GetRuntimeByType("pi").ShouldBeOfType<PiHarnessRuntime>();
    }
}
