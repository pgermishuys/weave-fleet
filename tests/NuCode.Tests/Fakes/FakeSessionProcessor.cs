using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NuCode.Sessions;

namespace NuCode.Fakes;

internal sealed class FakeSessionProcessor : ISessionProcessor
{
    private Func<AIAgent, AssistantMessage, IEnumerable<ChatMessage>, NuCodeAgentSession, CancellationToken, Task<ProcessResult>>? _handler;

    /// <summary>
    /// Sets the handler to invoke when <see cref="ProcessAsync"/> is called.
    /// </summary>
    public void OnProcess(
        Func<AIAgent, AssistantMessage, IEnumerable<ChatMessage>, NuCodeAgentSession, CancellationToken, Task<ProcessResult>> handler) =>
        _handler = handler;

    /// <summary>
    /// Sets a simple return value for <see cref="ProcessAsync"/>.
    /// </summary>
    public void SetResult(ProcessResult result) =>
        _handler = (_, _, _, _, _) => Task.FromResult(result);

    public Task<ProcessResult> ProcessAsync(
        AIAgent agent,
        AssistantMessage assistantMessage,
        IEnumerable<ChatMessage> chatMessages,
        NuCodeAgentSession session,
        CancellationToken ct) =>
        _handler?.Invoke(agent, assistantMessage, chatMessages, session, ct)
            ?? Task.FromResult(ProcessResult.Stop);
}
