using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

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
    private readonly string _fleetSessionId;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly string? _ownerUserId;

    public TestHarnessSession(string instanceId, TestScenario scenario)
        : this(instanceId, scenario, instanceId, scopeFactory: null, ownerUserId: null)
    {
    }

    public TestHarnessSession(
        string instanceId,
        TestScenario scenario,
        string fleetSessionId,
        IServiceScopeFactory? scopeFactory,
        string? ownerUserId)
    {
        InstanceId = instanceId;
        HarnessType = "opencode";
        _scenario = scenario;
        _fleetSessionId = fleetSessionId;
        _scopeFactory = scopeFactory;
        _ownerUserId = ownerUserId;
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

        await PersistUserPromptAsync(text).ConfigureAwait(false);

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
        => PushEventCoreAsync(evt, ct);

    /// <summary>Signal the subscription stream is complete (no more events).</summary>
    public void CompleteStream() => _channel.Writer.TryComplete();

    private async ValueTask PushEventCoreAsync(HarnessEvent evt, CancellationToken ct)
    {
        if (await TryHandleDurableEventAsync(evt).ConfigureAwait(false))
            return;

        await _channel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
    }

    private async Task PersistUserPromptAsync(string text)
    {
        if (_scopeFactory is null || string.IsNullOrWhiteSpace(_ownerUserId))
            return;

        using var scope = _scopeFactory.CreateScope();
        var writer = scope.ServiceProvider.GetService<SessionActivityWriteService>();
        if (writer is null)
            return;

        var messageId = Guid.NewGuid().ToString("N");
        var createdAt = DateTimeOffset.UtcNow;
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = messageId,
                sessionID = _fleetSessionId,
                role = "user",
                time = new { created = createdAt.ToUnixTimeMilliseconds() }
            }
        });

        var partsJson = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new { type = "text", kind = 0, text }
        });

        var persisted = new PersistedMessage
        {
            Id = messageId,
            SessionId = _fleetSessionId,
            Role = "user",
            PartsJson = partsJson,
            Timestamp = createdAt.ToString("O"),
            CreatedAt = createdAt.ToString("O")
        };

        await writer.WriteAsync(
            new SessionActivityWriteRequest
            {
                MessagesToUpsert = [persisted],
                OutboxMessages =
                [
                    new OutboxMessage
                    {
                        Topic = $"session:{_fleetSessionId}",
                        Type = "message.updated",
                        Payload = MessagePersistenceService.SerializePayload(payload),
                        UserId = _ownerUserId,
                        CreatedAt = createdAt.ToString("O"),
                        AvailableAt = createdAt.ToString("O")
                    }
                ]
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> TryHandleDurableEventAsync(HarnessEvent evt)
    {
        if (_scopeFactory is null || string.IsNullOrWhiteSpace(_ownerUserId))
            return false;

        if (evt.Type is not ("message.updated" or "message.part.updated"))
            return false;

        using var scope = _scopeFactory.CreateScope();
        var writer = scope.ServiceProvider.GetService<SessionActivityWriteService>();
        var messageRepo = scope.ServiceProvider.GetService<IMessageRepository>();
        if (writer is null || messageRepo is null || !evt.Payload.HasValue)
            return false;

        if (evt.Type == "message.updated")
        {
            var info = evt.Payload.Value.GetProperty("info");
            var messageId = info.GetProperty("id").GetString();
            var role = info.GetProperty("role").GetString() ?? "assistant";
            if (string.IsNullOrWhiteSpace(messageId))
                return false;

            var existing = await messageRepo.GetByIdAsync(messageId, _fleetSessionId).ConfigureAwait(false);
            var persisted = existing ?? new PersistedMessage
            {
                Id = messageId,
                SessionId = _fleetSessionId,
                Role = role,
                PartsJson = "[]",
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            };

            await writer.WriteAsync(
                new SessionActivityWriteRequest
                {
                    MessagesToUpsert = [persisted],
                    OutboxMessages =
                    [
                        new OutboxMessage
                        {
                            Topic = $"session:{_fleetSessionId}",
                            Type = evt.Type,
                            Payload = MessagePersistenceService.SerializePayload(evt.Payload.Value),
                            UserId = _ownerUserId,
                            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                            AvailableAt = DateTimeOffset.UtcNow.ToString("O")
                        }
                    ]
                },
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        var part = evt.Payload.Value.GetProperty("part");
        var durableMessageId = part.GetProperty("messageID").GetString();
        if (string.IsNullOrWhiteSpace(durableMessageId))
            return false;

        var existingMessage = await messageRepo.GetByIdAsync(durableMessageId, _fleetSessionId).ConfigureAwait(false);
        var text = part.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty;
        var partsJson = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new { type = "text", kind = 0, text }
        });
        var updated = existingMessage is null
            ? new PersistedMessage
            {
                Id = durableMessageId,
                SessionId = _fleetSessionId,
                Role = "assistant",
                PartsJson = partsJson,
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            }
            : new PersistedMessage
            {
                Id = existingMessage.Id,
                SessionId = existingMessage.SessionId,
                Role = existingMessage.Role,
                PartsJson = partsJson,
                Timestamp = existingMessage.Timestamp,
                CreatedAt = existingMessage.CreatedAt,
                AgentName = existingMessage.AgentName
            };

        await writer.WriteAsync(
            new SessionActivityWriteRequest
            {
                MessagesToUpsert = [updated],
                OutboxMessages =
                [
                    new OutboxMessage
                    {
                        Topic = $"session:{_fleetSessionId}",
                        Type = evt.Type,
                        Payload = MessagePersistenceService.SerializePayload(evt.Payload.Value),
                        UserId = _ownerUserId,
                        CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                        AvailableAt = DateTimeOffset.UtcNow.ToString("O")
                    }
                ]
            },
            CancellationToken.None).ConfigureAwait(false);
        return true;
    }
}
