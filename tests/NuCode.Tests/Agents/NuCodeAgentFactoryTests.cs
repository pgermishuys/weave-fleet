using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NuCode.Fakes;
using NuCode.Agents;

namespace NuCode;

public sealed class NuCodeAgentFactoryTests
{
    private static NuCodeAgentFactory CreateFactory() => new();

    private static List<AITool> CreateTestTools()
    {
        return
        [
            AIFunctionFactory.Create(() => "read-result", "read", "Read a file"),
            AIFunctionFactory.Create(() => "write-result", "write", "Write a file"),
            AIFunctionFactory.Create(() => "edit-result", "edit", "Edit a file"),
            AIFunctionFactory.Create(() => "bash-result", "bash", "Run a command"),
            AIFunctionFactory.Create(() => "grep-result", "grep", "Search contents"),
            AIFunctionFactory.Create(() => "glob-result", "glob", "Find files"),
        ];
    }

    [Fact]
    public void CreateAgentReturnsAgentWithProfileName()
    {
        var factory = CreateFactory();
        var chatClient = new FakeChatClient();
        var profile = BuiltInAgents.Build();
        var tools = CreateTestTools();

        var agent = factory.CreateAgent(profile, chatClient, tools);

        agent.ShouldNotBeNull();
        agent.Name.ShouldBe("build");
    }

    [Fact]
    public void CreateAgentWithAllowedToolsFiltersCorrectly()
    {
        var factory = CreateFactory();
        var chatClient = new FakeChatClient();
        var profile = new AgentProfile
        {
            Name = "readonly",
            AllowedTools = ["read", "grep", "glob"],
        };
        var tools = CreateTestTools();

        var agent = factory.CreateAgent(profile, chatClient, tools);

        // Verify the ChatOptions has only 3 tools
        var chatOptions = agent.GetService<ChatOptions>();
        chatOptions.ShouldNotBeNull();
        chatOptions.Tools.ShouldNotBeNull();
        chatOptions.Tools.Count.ShouldBe(3);
    }

    [Fact]
    public void CreateAgentWithDeniedToolsExcludesCorrectly()
    {
        var factory = CreateFactory();
        var chatClient = new FakeChatClient();
        var profile = new AgentProfile
        {
            Name = "safe",
            DeniedTools = ["bash", "write"],
        };
        var tools = CreateTestTools();

        var agent = factory.CreateAgent(profile, chatClient, tools);

        var chatOptions = agent.GetService<ChatOptions>();
        chatOptions.ShouldNotBeNull();
        chatOptions.Tools.ShouldNotBeNull();
        chatOptions.Tools.Count.ShouldBe(4);
    }

    [Fact]
    public void CreateAgentSetsTemperature()
    {
        var factory = CreateFactory();
        var chatClient = new FakeChatClient();
        var profile = new AgentProfile
        {
            Name = "creative",
            Temperature = 0.9,
        };

        var agent = factory.CreateAgent(profile, chatClient, []);

        var chatOptions = agent.GetService<ChatOptions>();
        chatOptions.ShouldNotBeNull();
        chatOptions.Temperature.ShouldBe(0.9f);
    }

    [Fact]
    public void CreateAgentSetsTopP()
    {
        var factory = CreateFactory();
        var chatClient = new FakeChatClient();
        var profile = new AgentProfile
        {
            Name = "nucleus",
            TopP = 0.95,
        };

        var agent = factory.CreateAgent(profile, chatClient, []);

        var chatOptions = agent.GetService<ChatOptions>();
        chatOptions.ShouldNotBeNull();
        chatOptions.TopP.ShouldBe(0.95f);
    }

    [Fact]
    public void CreateAgentSetsSystemPrompt()
    {
        var factory = CreateFactory();
        var chatClient = new FakeChatClient();
        var profile = new AgentProfile
        {
            Name = "custom",
            SystemPrompt = "You are a helpful assistant.",
        };

        var agent = factory.CreateAgent(profile, chatClient, []);

        ((ChatClientAgent)agent).Instructions.ShouldBe("You are a helpful assistant.");
    }

    [Fact]
    public void CreateAgentWithNoToolFilterIncludesAllTools()
    {
        var factory = CreateFactory();
        var chatClient = new FakeChatClient();
        var profile = new AgentProfile { Name = "all" };
        var tools = CreateTestTools();

        var agent = factory.CreateAgent(profile, chatClient, tools);

        var chatOptions = agent.GetService<ChatOptions>();
        chatOptions.ShouldNotBeNull();
        chatOptions.Tools.ShouldNotBeNull();
        chatOptions.Tools.Count.ShouldBe(6);
    }

    [Fact]
    public void CreateAgentSetsModelId()
    {
        var factory = CreateFactory();
        var chatClient = new FakeChatClient();
        var profile = new AgentProfile
        {
            Name = "specific-model",
            ModelId = "gpt-4o",
        };

        var agent = factory.CreateAgent(profile, chatClient, []);

        var chatOptions = agent.GetService<ChatOptions>();
        chatOptions.ShouldNotBeNull();
        chatOptions.ModelId.ShouldBe("gpt-4o");
    }

    [Fact]
    public void FactoryIsResolvableThroughDI()
    {
        var services = new ServiceCollection();
        services.AddNuCode();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<INuCodeAgentFactory>();

        factory.ShouldNotBeNull();
    }
}
