using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace NuCode.Agents;

/// <summary>
/// Factory for creating configured AI agents from agent profiles.
/// </summary>
public interface INuCodeAgentFactory
{
    /// <summary>
    /// Creates an AI agent configured according to the given profile.
    /// </summary>
    /// <param name="profile">The agent profile to configure the agent from.</param>
    /// <param name="chatClient">The LLM chat client to use.</param>
    /// <param name="availableTools">The full set of available tools. The factory filters them per the profile.</param>
    /// <returns>A configured <see cref="AIAgent"/> ready for use.</returns>
    AIAgent CreateAgent(AgentProfile profile, IChatClient chatClient, IReadOnlyList<AITool> availableTools);
}
