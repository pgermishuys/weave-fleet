using System.Runtime.CompilerServices;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Acp;

/// <summary>
/// The hidden child session of a subagent Copilot ran: it has no agent process. Its conversation is the one its parent's
/// session kept for the <c>task</c> call (<see cref="AcpHarnessSession.ChildMessages"/>), and its live events come out of
/// the parent's session, tagged with this session's id. It can't take a prompt: the subagent isn't Fleet's to talk to.
/// </summary>
/// <param name="findParent">The parent's live session, woken when it isn't live; null when it can't be.</param>
internal sealed class AcpChildSession(string instanceId, string callId, Func<Task<AcpHarnessSession?>> findParent) : IHarnessSession
{
    private readonly CancellationTokenSource _disposed = new();

    public string InstanceId { get; } = instanceId;

    public int? ProcessId => null;

    public string? ResumeToken => callId;

    public string HarnessType => "copilot";

    public HarnessSessionStatus Status => HarnessSessionStatus.Idle;

    public async Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        var parent = await findParent().ConfigureAwait(false);
        return AcpHarnessSession.Page(parent?.ChildMessages(callId) ?? [], query);
    }

    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        try
        {
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        yield break;
    }

    public Task WaitForEventSubscriptionAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Healthy: it has no process to lose, and its conversation wakes the parent when read.</summary>
    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct) => Task.FromResult(new HealthCheckResult(true, null));

    public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
        => throw new NotSupportedException("A subagent's conversation can't take a prompt.");

    public Task AbortAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string?> GetActivityStatusAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> SendCommandAsync(CommandOptions options, CancellationToken ct)
        => throw new NotSupportedException("A subagent's conversation doesn't take slash commands.");

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
        => throw new NotSupportedException("A subagent's conversation doesn't ask questions.");

    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
        => throw new NotSupportedException("A subagent's conversation doesn't ask questions.");

    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderInfo>>([]);

    public Task StopAsync(CancellationToken ct) => DisposeAsync().AsTask();

    public Task DeleteAsync(CancellationToken ct) => StopAsync(ct);

    public ValueTask DisposeAsync()
    {
        _disposed.Cancel();
        return ValueTask.CompletedTask;
    }
}
