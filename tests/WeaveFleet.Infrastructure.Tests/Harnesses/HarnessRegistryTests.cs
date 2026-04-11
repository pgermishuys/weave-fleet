using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Tests.Harnesses;

public sealed class HarnessRegistryTests
{
    [Fact]
    public void GetAll_WhenEmpty_ReturnsEmptyList()
    {
        var registry = new HarnessRegistry([]);
        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public void GetByType_ReturnsMatchingHarness()
    {
        var harness = new FakeHarness("opencode", "OpenCode");
        var registry = new HarnessRegistry([harness]);

        var found = registry.GetByType("opencode");
        found.ShouldNotBeNull();
        found.Type.ShouldBe("opencode");
    }

    [Fact]
    public void GetByType_IsCaseInsensitive()
    {
        var harness = new FakeHarness("opencode", "OpenCode");
        var registry = new HarnessRegistry([harness]);

        registry.GetByType("OpenCode").ShouldNotBeNull();
        registry.GetByType("OPENCODE").ShouldNotBeNull();
    }

    [Fact]
    public void GetByType_ReturnsNullWhenNotFound()
    {
        var registry = new HarnessRegistry([]);
        registry.GetByType("nonexistent").ShouldBeNull();
    }

    [Fact]
    public async Task GetAvailabilityAsync_AggregatesAllHarnesses()
    {
        var h1 = new FakeHarness("opencode", "OpenCode", available: true);
        var h2 = new FakeHarness("claude-code", "Claude Code", available: false, reason: "Binary not found");
        var registry = new HarnessRegistry([h1, h2]);

        var results = await registry.GetAvailabilityAsync(CancellationToken.None);

        results.Count.ShouldBe(2);
        results[0].Type.ShouldBe("opencode");
        results[0].Available.ShouldBeTrue();
        results[1].DisplayName.ShouldBe("Claude Code");
        results[1].Available.ShouldBeFalse();
        results[1].Reason.ShouldBe("Binary not found");
    }

    /// <summary>Minimal fake for testing the registry — NOT a real harness.</summary>
    private sealed class FakeHarness(
        string type,
        string displayName,
        bool available = true,
        string? reason = null) : IHarness
    {
        public string Type => type;
        public string DisplayName => displayName;
        public HarnessCapabilities Capabilities => new();

        public Task<HarnessAvailability> CheckAvailabilityAsync(CancellationToken ct)
            => Task.FromResult(new HarnessAvailability(available, reason));

        public Task<RuntimePreparation> PrepareRuntimeAsync(RuntimePreparationContext context, CancellationToken ct)
            => Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new FakeLaunchArtifacts()));

        public Task<IHarnessInstance> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
            => throw new NotSupportedException("FakeHarness cannot spawn.");

        public Task<IHarnessInstance> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
            => throw new NotSupportedException("FakeHarness cannot resume.");
    }

    private sealed record FakeLaunchArtifacts : RuntimeLaunchArtifacts;
}
