using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NuCode.Agents;

namespace NuCode.Fakes;

internal sealed class FakeNuCodeAgentFactory : INuCodeAgentFactory
{
    private readonly IChatClient _chatClient;

    public FakeNuCodeAgentFactory(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    /// <summary>
    /// The tools passed to the last <see cref="CreateAgent"/> call, for test verification.
    /// </summary>
    public IReadOnlyList<AITool>? CapturedTools { get; private set; }

    public AIAgent CreateAgent(
        AgentProfile profile,
        IChatClient chatClient,
        IReadOnlyList<AITool> availableTools)
    {
        CapturedTools = availableTools;
        return _chatClient.AsAIAgent(new ChatClientAgentOptions { Name = profile.Name });
    }
}
