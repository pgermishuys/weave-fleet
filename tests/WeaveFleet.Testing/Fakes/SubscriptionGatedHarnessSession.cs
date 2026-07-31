using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

/// <summary>
/// A fake harness session that gates event delivery on subscription readiness.
/// Events emitted before SubscribeAsync is called are LOST, simulating the real-world
/// race condition where a harness emits events before the relay pump has attached.
/// Also tracks whether WaitForEventSubscriptionAsync was called before SendPromptAsync.
/// </summary>
public sealed class SubscriptionGatedHarnessSession : IHarnessSession
{
    private readonly Channel<HarnessEvent> _channel = Channel.CreateUnbounded<HarnessEvent>();
    private readonly TaskCompletionSource _subscriptionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<HarnessEvent> _preSubscriptionEvents = [];
    private int _subscribed;
    private int _readinessAwaited;
    private int _promptSent;

    public SubscriptionGatedHarnessSession(string instanceId)
    {
        InstanceId = instanceId;
    }

    public string InstanceId { get; }
    public int? ProcessId { get; set; }
    public string HarnessType { get; set; } = "opencode";
    public string? ResumeToken { get; set; }
    public HarnessSessionStatus Status { get; set; } = HarnessSessionStatus.Running;

    public List<(string Text, PromptOptions? Options)> SendPromptCalls { get; } = [];

    /// <summary>
    /// Returns true if WaitForEventSubscriptionAsync was called before SendPromptAsync.
    /// </summary>
    public bool WasReadinessAwaitedBeforeSendPrompt => 
        Volatile.Read(ref _readinessAwaited) == 1 && Volatile.Read(ref _promptSent) == 1;

    /// <summary>
    /// Emit an event. If SubscribeAsync has not been called yet, the event is LOST.
    /// </summary>
    public void Emit(HarnessEvent evt)
    {
        if (Volatile.Read(ref _subscribed) == 1)
        {
            _channel.Writer.TryWrite(evt);
        }
        else
        {
            // Event is lost — simulates the race condition
            lock (_preSubscriptionEvents)
            {
                _preSubscriptionEvents.Add(evt);
            }
        }
    }

    public int PreSubscriptionEventCount
    {
        get
        {
            lock (_preSubscriptionEvents)
            {
                return _preSubscriptionEvents.Count;
            }
        }
    }

    public void Complete() => _channel.Writer.Complete();

    public Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        Volatile.Write(ref _promptSent, 1);
        SendPromptCalls.Add((text, options));
        return Task.CompletedTask;
    }

    public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
        => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct)
        => Task.CompletedTask;

    public Task DeleteAsync(CancellationToken ct)
        => Task.CompletedTask;

    public Task AbortAsync(CancellationToken ct)
        => Task.CompletedTask;

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
        => Task.CompletedTask;

    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
        => Task.CompletedTask;

    public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
        => Task.FromResult(new MessagePage([], false));

    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Signal that subscription is now ready
        Volatile.Write(ref _subscribed, 1);
        _subscriptionReady.TrySetResult();

        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
            yield return evt;
    }

    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(new HealthCheckResult(true, null));

    public Task WaitForEventSubscriptionAsync(CancellationToken ct)
    {
        Volatile.Write(ref _readinessAwaited, 1);
        return _subscriptionReady.Task.WaitAsync(ct);
    }

    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderInfo>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
