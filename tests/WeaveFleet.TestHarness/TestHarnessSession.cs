using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.TestHarness;

/// <summary>
/// A mock <see cref="IHarnessSession"/> that drives test scenarios.
/// Pushes pre-configured <see cref="HarnessEvent"/> objects into an internal channel
/// when <see cref="SendPromptAsync"/> is called; <see cref="SubscribeAsync"/> yields them.
/// </summary>
public sealed class TestHarnessSession : IHarnessSession
{
    private readonly TestScenario _scenario;
    private readonly Channel<HarnessEvent> _channel;
    private volatile HarnessSessionStatus _status;
    private CancellationTokenSource? _promptCts;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TestHarnessSession(string instanceId, TestScenario scenario)
    {
        InstanceId = instanceId;
        HarnessType = "opencode";
        _scenario = scenario;
        _status = scenario.InitialStatus;

        // Unbounded channel — tests emit a bounded number of events.
        _channel = Channel.CreateUnbounded<HarnessEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    }

    // ── IHarnessSession ────────────────────────────────────────────────────

    /// <inheritdoc/>
    public string InstanceId { get; }

    /// <inheritdoc/>
    public string HarnessType { get; }

    /// <inheritdoc/>
    public string? ResumeToken => null;

    /// <inheritdoc/>
    public HarnessSessionStatus Status => _status;

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(new HealthCheckResult(Healthy: true, Message: null));

    /// <inheritdoc/>
    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    /// <inheritdoc/>
    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

    /// <inheritdoc/>
    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderInfo>>([]);

    /// <inheritdoc/>
    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        if (_scenario.ThrowOnSendPrompt)
            throw new InvalidOperationException("TestHarness: configured to fail on SendPromptAsync.");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Cancel any in-flight prompt
            _promptCts?.Cancel();
            _promptCts?.Dispose();
            _promptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var promptToken = _promptCts.Token;

            // Dequeue the next response sequence (or use empty default)
            IReadOnlyList<ScenarioEvent> events = _scenario.PromptResponses.Count > 0
                ? _scenario.PromptResponses.Dequeue()
                : [];

            // Fire and forget: emit events in background so caller returns immediately
            _ = Task.Run(() => EmitEventsAsync(events, promptToken), promptToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        // Sanitize arguments: collapse newlines to spaces to prevent prompt injection
        var sanitizedArgs = options.Arguments?.ReplaceLineEndings(" ");

        var text = string.IsNullOrWhiteSpace(sanitizedArgs)
            ? $"/{options.Command}"
            : $"/{options.Command} {sanitizedArgs}";

        var promptOptions = options.Agent is not null || options.ModelId is not null
            ? new PromptOptions { Agent = options.Agent, ModelId = options.ModelId }
            : null;

        return SendPromptAsync(text, promptOptions, ct);
    }

    /// <inheritdoc/>
    public Task AbortAsync(CancellationToken ct)
    {
        _promptCts?.Cancel();
        _status = HarnessSessionStatus.Idle;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        var messages = (IReadOnlyList<HarnessMessage>)_scenario.Messages;

        if (query?.Before is not null)
        {
            var idx = messages.TakeWhile(m => m.Id != query.Before).Count();
            messages = messages.Take(idx).ToList();
        }

        var limit = query?.Limit ?? messages.Count;
        var page = messages.TakeLast(limit).ToList();
        var hasMore = page.Count < messages.Count;

        return Task.FromResult(new MessagePage(page, hasMore));
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken ct)
    {
        _status = HarnessSessionStatus.Stopped;
        _channel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAsync(CancellationToken ct) => StopAsync(ct);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _promptCts?.Cancel();
        _promptCts?.Dispose();
        _lock.Dispose();
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    // ── Internal helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Emits the scenario events into the channel with their configured delays.
    /// Transitions status: Starting → Running → (after events) → Idle.
    /// </summary>
    private async Task EmitEventsAsync(IReadOnlyList<ScenarioEvent> events, CancellationToken ct)
    {
        _status = HarnessSessionStatus.Running;
        try
        {
            foreach (var scenarioEvent in events)
            {
                ct.ThrowIfCancellationRequested();

                if (scenarioEvent.Delay > TimeSpan.Zero)
                    await Task.Delay(scenarioEvent.Delay, ct).ConfigureAwait(false);

                await _channel.Writer.WriteAsync(scenarioEvent.Event, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Aborted — status already set to Idle by AbortAsync
            return;
        }
        finally
        {
            _status = HarnessSessionStatus.Idle;
        }
    }

    /// <summary>
    /// Directly push an event into the channel. Useful for test setup code
    /// that needs to simulate server-initiated events.
    /// </summary>
    public ValueTask PushEventAsync(HarnessEvent evt, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(evt, ct);

    /// <summary>Signal the subscription stream is complete (no more events).</summary>
    public void CompleteStream() => _channel.Writer.TryComplete();
}

