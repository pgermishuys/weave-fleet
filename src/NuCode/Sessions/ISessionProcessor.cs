using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace NuCode.Sessions;

/// <summary>
/// Processes a single streaming agent invocation: streams responses from the agent,
/// creates/updates message parts (text, tool calls, reasoning), handles errors,
/// and determines whether the conversation loop should continue, stop, or compact.
/// </summary>
public interface ISessionProcessor
{
    /// <summary>
    /// Runs one streaming iteration of the agent and creates message parts from the response.
    /// </summary>
    /// <param name="agent">The configured agent to invoke.</param>
    /// <param name="assistantMessage">The assistant message being built for this iteration.</param>
    /// <param name="chatMessages">The full conversation history in Agent Framework format.</param>
    /// <param name="session">The agent session state.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ProcessResult"/> indicating whether to continue the loop, stop, or compact.
    /// </returns>
    Task<ProcessResult> ProcessAsync(
        AIAgent agent,
        AssistantMessage assistantMessage,
        IEnumerable<ChatMessage> chatMessages,
        NuCodeAgentSession session,
        CancellationToken ct);
}
