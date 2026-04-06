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
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void GetByType_ReturnsMatchingHarness()
    {
        var harness = new FakeHarness("opencode", "OpenCode");
        var registry = new HarnessRegistry([harness]);

        var found = registry.GetByType("opencode");
        Assert.NotNull(found);
        Assert.Equal("opencode", found.Type);
    }

    [Fact]
    public void GetByType_IsCaseInsensitive()
    {
        var harness = new FakeHarness("opencode", "OpenCode");
        var registry = new HarnessRegistry([harness]);

        Assert.NotNull(registry.GetByType("OpenCode"));
        Assert.NotNull(registry.GetByType("OPENCODE"));
    }

    [Fact]
    public void GetByType_ReturnsNullWhenNotFound()
    {
        var registry = new HarnessRegistry([]);
        Assert.Null(registry.GetByType("nonexistent"));
    }

    [Fact]
    public async Task GetAvailabilityAsync_AggregatesAllHarnesses()
    {
        var h1 = new FakeHarness("opencode", "OpenCode", available: true);
        var h2 = new FakeHarness("claude-code", "Claude Code", available: false, reason: "Binary not found");
        var registry = new HarnessRegistry([h1, h2]);

        var results = await registry.GetAvailabilityAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("opencode", results[0].Type);
        Assert.True(results[0].Available);
        Assert.Equal("Claude Code", results[1].DisplayName);
        Assert.False(results[1].Available);
        Assert.Equal("Binary not found", results[1].Reason);
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

        public Task<IHarnessInstance> SpawnAsync(HarnessSpawnOptions options, CancellationToken ct)
            => throw new NotSupportedException("FakeHarness cannot spawn.");

        public Task<IHarnessInstance> ResumeAsync(HarnessResumeOptions options, CancellationToken ct)
            => throw new NotSupportedException("FakeHarness cannot resume.");
    }
}
