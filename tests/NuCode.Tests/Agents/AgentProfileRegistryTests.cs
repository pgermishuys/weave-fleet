using Microsoft.Extensions.DependencyInjection;
using NuCode.Agents;

namespace NuCode;

public sealed class AgentProfileRegistryTests
{
    private static AgentProfileRegistry CreateRegistry() => new();

    [Fact]
    public void GetReturnsBuiltInProfileByName()
    {
        var registry = CreateRegistry();

        var build = registry.Get("build");

        build.ShouldNotBeNull();
        build.Name.ShouldBe("build");
        build.IsNative.ShouldBeTrue();
    }

    [Fact]
    public void GetIsCaseInsensitive()
    {
        var registry = CreateRegistry();

        var profile = registry.Get("BUILD");

        profile.ShouldNotBeNull();
        profile.Name.ShouldBe("build");
    }

    [Fact]
    public void GetReturnsNullForUnknownName()
    {
        var registry = CreateRegistry();

        var result = registry.Get("nonexistent");

        result.ShouldBeNull();
    }

    [Fact]
    public void GetAllReturnsAllBuiltInAgents()
    {
        var registry = CreateRegistry();

        var all = registry.GetAll();

        all.Count.ShouldBe(7);
    }

    [Fact]
    public void GetVisibleExcludesHiddenAgents()
    {
        var registry = CreateRegistry();

        var visible = registry.GetVisible();

        visible.Count.ShouldBe(4);
        visible.ShouldAllBe(p => !p.IsHidden);
    }

    [Fact]
    public void RegisterAddsCustomProfile()
    {
        var registry = CreateRegistry();
        var custom = new AgentProfile
        {
            Name = "custom-agent",
            Description = "A custom agent",
            Mode = AgentMode.SubAgent,
        };

        registry.Register(custom);

        var retrieved = registry.Get("custom-agent");
        retrieved.ShouldNotBeNull();
        retrieved.Name.ShouldBe("custom-agent");
        registry.GetAll().Count.ShouldBe(8);
    }

    [Fact]
    public void RegisterThrowsForDuplicateName()
    {
        var registry = CreateRegistry();
        var duplicate = new AgentProfile { Name = "build" };

        var ex = Should.Throw<InvalidOperationException>(() => registry.Register(duplicate));
        ex.Message.ShouldContain("build");
    }

    [Fact]
    public void TryOverrideModifiesExistingProfile()
    {
        var registry = CreateRegistry();

        var result = registry.TryOverride("build", p => p with { Temperature = 0.3 });

        result.ShouldBeTrue();
        var updated = registry.Get("build");
        updated.ShouldNotBeNull();
        updated.Temperature.ShouldBe(0.3);
        updated.Name.ShouldBe("build"); // Name preserved
    }

    [Fact]
    public void TryOverrideReturnsFalseForUnknownName()
    {
        var registry = CreateRegistry();

        var result = registry.TryOverride("nonexistent", p => p with { Temperature = 0.5 });

        result.ShouldBeFalse();
    }

    [Fact]
    public void RegistryIsResolvableThroughDI()
    {
        var services = new ServiceCollection();
        services.AddNuCode();

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IAgentProfileRegistry>();

        registry.ShouldNotBeNull();
        registry.GetAll().Count.ShouldBe(7);
    }
}
