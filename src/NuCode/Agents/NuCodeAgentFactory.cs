using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuCode.Configuration;
using NuCode.Tools;

namespace NuCode.Agents;

/// <summary>
/// Default implementation of <see cref="INuCodeAgentFactory"/>.
/// Creates configured <see cref="AIAgent"/> instances from agent profiles using the Microsoft Agent Framework.
/// </summary>
internal sealed class NuCodeAgentFactory : INuCodeAgentFactory
{
    private readonly ILoggerFactory? _loggerFactory;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IOptionsMonitor<NuCodeConfig>? _configMonitor;

    public NuCodeAgentFactory(ILoggerFactory? loggerFactory = null, IServiceProvider? serviceProvider = null, IOptionsMonitor<NuCodeConfig>? configMonitor = null)
    {
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _configMonitor = configMonitor;
    }

    public AIAgent CreateAgent(AgentProfile profile, IChatClient chatClient, IReadOnlyList<AITool> availableTools)
    {
        var filteredTools = FilterTools(profile, availableTools);

        var chatOptions = new ChatOptions
        {
            Instructions = profile.SystemPrompt,
            Tools = filteredTools,
        };

        if (profile.Temperature.HasValue)
        {
            chatOptions.Temperature = (float)profile.Temperature.Value;
        }

        if (profile.TopP.HasValue)
        {
            chatOptions.TopP = (float)profile.TopP.Value;
        }

        if (profile.ModelId is not null)
        {
            chatOptions.ModelId = profile.ModelId;
        }

        var agentOptions = new ChatClientAgentOptions
        {
            Name = profile.Name,
            Description = profile.Description,
            ChatOptions = chatOptions,
        };

        var agent = chatClient.AsAIAgent(agentOptions, _loggerFactory, _serviceProvider);

        if (_configMonitor is not null)
        {
            return agent.AsBuilder()
                .Use(TimeoutMiddleware.CreateMiddleware(_configMonitor))
                .Build();
        }

        return agent;
    }

    private static List<AITool> FilterTools(AgentProfile profile, IReadOnlyList<AITool> availableTools)
    {
        var result = new List<AITool>(availableTools.Count);

        foreach (var tool in availableTools)
        {
            var toolName = GetToolName(tool);
            if (toolName is null)
            {
                result.Add(tool);
                continue;
            }

            // If AllowedTools is set, only include tools in that set
            if (profile.AllowedTools is not null && !profile.AllowedTools.Contains(toolName))
            {
                continue;
            }

            // If DeniedTools is set, exclude tools in that set
            if (profile.DeniedTools is not null && profile.DeniedTools.Contains(toolName))
            {
                continue;
            }

            result.Add(tool);
        }

        return result;
    }

    private static string? GetToolName(AITool tool) =>
        tool switch
        {
            AIFunction function => function.Name,
            _ => tool.GetType().Name,
        };
}
