using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeHarnessSession : IHarnessSession
{
    private readonly Channel<HarnessEvent> _channel = Channel.CreateUnbounded<HarnessEvent>();

    public FakeHarnessSession(string instanceId)
    {
        InstanceId = instanceId;
    }

    // ── Configurable properties ──────────────────────────────────────────────

    public string InstanceId { get; }
    public int? ProcessId { get; set; }
    public string HarnessType { get; set; } = "opencode";
    public string? ResumeToken { get; set; }
    public HarnessSessionStatus Status { get; set; } = HarnessSessionStatus.Running;

    // ── Call-tracking for assertions ─────────────────────────────────────────

    // ConcurrentBag allows concurrent adds from multiple prompt tasks in concurrent activation tests.
    private readonly ConcurrentBag<(string Text, PromptOptions? Options)> _sendPromptCalls = [];
    private readonly ConcurrentBag<CommandOptions> _sendCommandCalls = [];

    /// <summary>Records each <see cref="SendPromptAsync"/> call for test assertions.</summary>
    public IReadOnlyList<(string Text, PromptOptions? Options)> SendPromptCalls => [.. _sendPromptCalls];

    /// <summary>Records each <see cref="SendCommandAsync"/> call for test assertions.</summary>
    public IReadOnlyList<CommandOptions> SendCommandCalls => [.. _sendCommandCalls];

    public bool StopCalled { get; private set; }
    public bool DeleteCalled { get; private set; }
    public bool AbortCalled { get; private set; }

    // ── Configurable behaviors ───────────────────────────────────────────────

    public Func<MessageQuery?, CancellationToken, Task<MessagePage>>? GetMessagesBehavior { get; set; }

    /// <summary>
    /// Optional override for <see cref="IHarnessSession.DeleteAsync"/>. Allows tests to assert that
    /// best-effort delete is called during rollback scenarios.
    /// </summary>
    public Func<CancellationToken, Task>? DeleteBehavior { get; set; }

    // ── Event emission (for streaming tests) ─────────────────────────────────

    public void Emit(HarnessEvent evt) => _channel.Writer.TryWrite(evt);
    public void Complete() => _channel.Writer.Complete();

    // ── IHarnessSession ──────────────────────────────────────────────────────

    public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        _sendPromptCalls.Add((text, options));
        return Task.CompletedTask;
    }

    public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        _sendCommandCalls.Add(options);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        StopCalled = true;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        DeleteCalled = true;
        return DeleteBehavior?.Invoke(ct) ?? Task.CompletedTask;
    }

    public Task AbortAsync(CancellationToken ct)
    {
        AbortCalled = true;
        return Task.CompletedTask;
    }

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
        => Task.CompletedTask;

    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
        => Task.CompletedTask;

    public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
        => GetMessagesBehavior?.Invoke(query, ct)
           ?? Task.FromResult(new MessagePage([], false));

    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(new HealthCheckResult(true, null));

    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderInfo>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
