using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NuCode.Agents;
using NuCode.Tools;

namespace NuCode;

public sealed class ToolRegistryTests
{
    private sealed class FakeTool : INuCodeTool
    {
        public FakeTool(string name, string description = "A test tool")
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }
        public string Description { get; }

        public AIFunction ToAIFunction() =>
            AIFunctionFactory.Create(() => "result", Name, Description);
    }

    [Fact]
    public void RegisterAndRetrieveTool()
    {
        var registry = new ToolRegistry();
        var tool = new FakeTool("read");

        registry.Register(tool);

        var retrieved = registry.Get("read");
        retrieved.ShouldNotBeNull();
        retrieved.Name.ShouldBe("read");
    }

    [Fact]
    public void RegisterDuplicateThrows()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("read"));

        var ex = Should.Throw<InvalidOperationException>(
            () => registry.Register(new FakeTool("read")));
        ex.Message.ShouldContain("read");
    }

    [Fact]
    public void GetAllReturnsRegisteredTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("read"));
        registry.Register(new FakeTool("write"));
        registry.Register(new FakeTool("edit"));

        var all = registry.GetAll();

        all.Count.ShouldBe(3);
    }

    [Fact]
    public void GetForProfileWithAllowedToolsFilters()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("read"));
        registry.Register(new FakeTool("write"));
        registry.Register(new FakeTool("edit"));
        registry.Register(new FakeTool("bash"));

        var profile = new AgentProfile
        {
            Name = "readonly",
            AllowedTools = ["read"],
        };

        var tools = registry.GetForProfile(profile);

        tools.ShouldHaveSingleItem();
        tools[0].Name.ShouldBe("read");
    }

    [Fact]
    public void GetForProfileWithDeniedToolsExcludes()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("read"));
        registry.Register(new FakeTool("write"));
        registry.Register(new FakeTool("bash"));

        var profile = new AgentProfile
        {
            Name = "safe",
            DeniedTools = ["bash"],
        };

        var tools = registry.GetForProfile(profile);

        tools.Count.ShouldBe(2);
        tools.ShouldNotContain(t => t.Name == "bash");
    }

    [Fact]
    public void GetForProfileWithNoFiltersReturnsAll()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeTool("read"));
        registry.Register(new FakeTool("write"));

        var profile = new AgentProfile { Name = "all" };

        var tools = registry.GetForProfile(profile);

        tools.Count.ShouldBe(2);
    }

    [Fact]
    public void GetReturnsNullForUnknownTool()
    {
        var registry = new ToolRegistry();

        var result = registry.Get("nonexistent");

        result.ShouldBeNull();
    }

    [Fact]
    public void RegistryIsResolvableThroughDI()
    {
        var services = new ServiceCollection();
        services.AddNuCode();

        var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IToolRegistry>();

        registry.ShouldNotBeNull();
    }
}
