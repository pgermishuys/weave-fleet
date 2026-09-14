using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private string? _lastQuestionMessageId;
    private JsonElement? _lastQuestionInput;

    // The session's messages as a real harness would report them: the scenario's
    // pre-loaded messages, plus prompts sent and message events emitted since.
    // Parts carry no ID of their own, so their event IDs are tracked alongside.
    private readonly Lock _messagesGate = new();
    private readonly List<HarnessMessage> _messages;
    private readonly Dictionary<string, List<string?>> _partIds = new(StringComparer.Ordinal);

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
        _messages = [.. scenario.Messages];
        foreach (var message in _messages)
            _partIds[message.Id] = [.. message.Parts.Select(_ => (string?)null)];

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
    public int? ProcessId => null;

    /// <inheritdoc/>
    public HarnessSessionStatus Status => _status;

    /// <inheritdoc/>
    public Task WaitForEventSubscriptionAsync(CancellationToken ct)
    {
        // The in-memory channel buffers events, so subscription is always ready.
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
        => Task.FromResult(new HealthCheckResult(Healthy: true, Message: null));

    /// <inheritdoc/>
    /// <remarks>A scripted answer naming the last prompt, so recaps can be checked without a model.</remarks>
    public Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct)
    {
        string? lastPrompt;
        lock (_messagesGate)
        {
            lastPrompt = _messages.LastOrDefault(m => m.Role == "user")?.Parts.OfType<TextPart>().FirstOrDefault()?.Text;
        }

        if (string.IsNullOrWhiteSpace(lastPrompt))
            return Task.FromResult<string?>(null);

        var asked = lastPrompt.Length > 80 ? string.Concat(lastPrompt.AsSpan(0, 80), "…") : lastPrompt;
        return Task.FromResult<string?>($"You asked the test harness \"{asked}\" and it replied. Next, send another prompt.");
    }

    /// <inheritdoc/>
    public Task<string?> GetActivityStatusAsync(CancellationToken ct)
        => Task.FromResult<string?>("idle");

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

            RecordUserPrompt(options?.MessageId ?? $"msg-user-{Guid.NewGuid():N}", text);

            // Dequeue the next response sequence (or use empty default)
            IReadOnlyList<ScenarioEvent> events = _scenario.PromptResponses.Count > 0
                ? _scenario.PromptResponses.Dequeue()
                : [];

            // Fire and forget: emit events in background so caller returns immediately
            _ = Task.Run(() => EmitEventsAsync(ApplyPromptText(events, text, null), promptToken), promptToken);
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
    public async Task AbortAsync(CancellationToken ct)
    {
        _promptCts?.Cancel();
        _status = HarnessSessionStatus.Idle;

        await PushEventCoreAsync(new HarnessEvent
        {
            Type = EventTypes.SessionStatus,
            SessionId = InstanceId,
            FleetSessionId = _fleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                sessionID = InstanceId,
                status = new { type = "idle" }
            })
        }, ct).ConfigureAwait(false);

        await PushEventCoreAsync(new HarnessEvent
        {
            Type = EventTypes.SessionIdle,
            SessionId = InstanceId,
            FleetSessionId = _fleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                sessionID = InstanceId
            })
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("AOT", "IL2026", Justification = "Test infrastructure only")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Test infrastructure only")]
    public async Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
    {
        LastAnswers = answers;

        // Emit a message.part.updated event that transitions the tool part to completed,
        // mimicking the real harness behaviour after an answer is accepted.
        var evt = new HarnessEvent
        {
            Type = "message.part.updated",
            SessionId = InstanceId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new
            {
                sessionID = InstanceId,
                part = new
                {
                    type = "tool",
                    id = requestId,
                    callID = requestId,
                    tool = "question",
                    sessionID = InstanceId,
                    messageID = _lastQuestionMessageId ?? "msg-question",
                    state = new
                    {
                        status = "completed",
                        input = _lastQuestionInput,
                        metadata = new { answers }
                    }
                }
            })
        };

        await PushEventCoreAsync(evt, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        List<HarnessMessage> messages;
        lock (_messagesGate)
            messages = [.. _messages];

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
    /// Durable events for assistant messages are persisted via
    /// <see cref="TryHandleDurableEventAsync"/> so tool parts (question, bash, etc.)
    /// are available when the frontend fetches messages via the REST API.
    /// User message echoes are skipped because the orchestrator
    /// already handles user message persistence at send time.
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

                // Persist durable events for assistant messages so tool parts are
                // available via REST. Skip user message echoes — those are already
                // persisted by PersistUserPromptAsync.
                if (IsDurableAssistantEvent(scenarioEvent.Event))
                    await TryHandleDurableEventAsync(scenarioEvent.Event).ConfigureAwait(false);

                // Track question tool context for AnswerQuestionAsync
                TryTrackQuestionContext(scenarioEvent.Event);
                TrackMessageEvent(scenarioEvent.Event);

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
    /// Returns true if the event is a durable event for an assistant message
    /// (message.updated with role=assistant, or message.part.updated for an
    /// assistant message). User message echoes return false.
    /// </summary>
    private static bool IsDurableAssistantEvent(HarnessEvent evt)
    {
        if (!evt.Payload.HasValue)
            return false;

        if (evt.Type == "message.updated")
        {
            if (!evt.Payload.Value.TryGetProperty("info", out var info))
                return false;
            if (!info.TryGetProperty("role", out var role))
                return false;
            return role.GetString() is "assistant";
        }

        return evt.Type is "message.part.updated";
    }

    private static List<ScenarioEvent> ApplyPromptText(IReadOnlyList<ScenarioEvent> events, string promptText, string? persistedPromptMessageId)
    {
        var updatedEvents = new List<ScenarioEvent>(events.Count);

        foreach (var scenarioEvent in events)
        {
            if (!scenarioEvent.Event.Payload.HasValue
                || !TryRewritePromptPayload(
                    scenarioEvent.Event.Type,
                    scenarioEvent.Event.Payload.Value,
                    promptText,
                    persistedPromptMessageId,
                    out var rewrittenPayload))
            {
                updatedEvents.Add(scenarioEvent);
                continue;
            }

            updatedEvents.Add(new ScenarioEvent
            {
                Event = scenarioEvent.Event with { Payload = rewrittenPayload },
                Delay = scenarioEvent.Delay,
            });
        }

        return updatedEvents;
    }

    [UnconditionalSuppressMessage("AOT", "IL2026", Justification = "Test infrastructure only")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Test infrastructure only")]
    private static bool TryRewritePromptPayload(
        string eventType,
        JsonElement payload,
        string promptText,
        string? persistedPromptMessageId,
        out JsonElement rewrittenPayload)
    {
        rewrittenPayload = payload;

        if (eventType == "message.updated")
            return TryRewritePromptMessagePayload(payload, persistedPromptMessageId, out rewrittenPayload);

        if (eventType != "message.part.updated")
            return false;

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("part", out var part)
            || part.ValueKind != JsonValueKind.Object
            || !part.TryGetProperty("text", out var text)
            || text.ValueKind != JsonValueKind.String
            || !string.Equals(text.GetString(), TestHarnessPromptTokens.UserPromptPlaceholder, StringComparison.Ordinal))
        {
            return false;
        }

        var sessionId = payload.TryGetProperty("sessionID", out var sessionIdEl) && sessionIdEl.ValueKind == JsonValueKind.String
            ? sessionIdEl.GetString()
            : null;

        rewrittenPayload = JsonSerializer.SerializeToElement(new
        {
            sessionID = sessionId,
            part = new
            {
                type = "text",
                id = persistedPromptMessageId is null
                    ? part.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null
                    : $"{persistedPromptMessageId}-text-0",
                sessionID = part.TryGetProperty("sessionID", out var partSessionEl) && partSessionEl.ValueKind == JsonValueKind.String ? partSessionEl.GetString() : sessionId,
                messageID = persistedPromptMessageId
                    ?? (part.TryGetProperty("messageID", out var messageIdEl) && messageIdEl.ValueKind == JsonValueKind.String ? messageIdEl.GetString() : null),
                text = promptText,
            }
        });

        return true;
    }

    [UnconditionalSuppressMessage("AOT", "IL2026", Justification = "Test infrastructure only")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Test infrastructure only")]
    private static bool TryRewritePromptMessagePayload(JsonElement payload, string? persistedPromptMessageId, out JsonElement rewrittenPayload)
    {
        rewrittenPayload = payload;

        if (string.IsNullOrWhiteSpace(persistedPromptMessageId)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("info", out var info)
            || !info.TryGetProperty("role", out var role)
            || !string.Equals(role.GetString(), "user", StringComparison.Ordinal))
        {
            return false;
        }

        var sessionId = info.TryGetProperty("sessionID", out var sessionEl) && sessionEl.ValueKind == JsonValueKind.String
            ? sessionEl.GetString()
            : null;

        var time = info.TryGetProperty("time", out var timeEl) && timeEl.ValueKind == JsonValueKind.Object
            ? timeEl.Clone()
            : JsonSerializer.SerializeToElement(new
            {
                created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

        rewrittenPayload = JsonSerializer.SerializeToElement(new
        {
            info = new
            {
                id = persistedPromptMessageId,
                sessionID = sessionId,
                role = "user",
                time,
            }
        });

        return true;
    }

    /// <summary>
    /// Directly push an event into the channel. Useful for test setup code
    /// that needs to simulate server-initiated events.
    /// </summary>
    public ValueTask PushEventAsync(HarnessEvent evt, CancellationToken ct = default)
        => PushEventCoreAsync(evt, ct);

    /// <summary>Signal the subscription stream is complete (no more events).</summary>
    public void CompleteStream() => _channel.Writer.TryComplete();

    /// <summary>
    /// Tracks the question context so that <see cref="AnswerQuestionAsync"/> can emit
    /// a properly-formed completion event. Call this after pushing the question tool part.
    /// </summary>
    public void SetQuestionContext(string messageId, JsonElement input)
    {
        _lastQuestionMessageId = messageId;
        _lastQuestionInput = input;
    }

    /// <summary>The answers passed to the most recent <see cref="AnswerQuestionAsync"/> call.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? LastAnswers { get; private set; }

    private async ValueTask PushEventCoreAsync(HarnessEvent evt, CancellationToken ct)
    {
        // Persist durable events to the DB so REST queries return them. The unified
        // fan-out subscriber is the single broadcast path, so every event also goes
        // through the channel — the relay publishes it to the event bus and the fan-out
        // subscriber forwards it to WebSocket clients.
        await TryHandleDurableEventAsync(evt).ConfigureAwait(false);
        await TryHandleDelegationEventAsync(evt).ConfigureAwait(false);
        TrackMessageEvent(evt);
        await _channel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
    }

    private void RecordUserPrompt(string messageId, string text)
    {
        lock (_messagesGate)
        {
            if (_partIds.ContainsKey(messageId))
                return;

            _messages.Add(new HarnessMessage
            {
                Id = messageId,
                Role = "user",
                Parts = [new TextPart(text)],
                Timestamp = DateTimeOffset.UtcNow,
            });
            _partIds[messageId] = [null];
        }
    }

    /// <summary>
    /// Applies a message event to <see cref="_messages"/>, so snapshots taken after it
    /// (a page load or a SignalR re-subscribe) include it, as they would with OpenCode.
    /// </summary>
    private void TrackMessageEvent(HarnessEvent evt)
    {
        if (!evt.Payload.HasValue || evt.Payload.Value.ValueKind != JsonValueKind.Object)
            return;

        var payload = evt.Payload.Value;
        lock (_messagesGate)
        {
            switch (evt.Type)
            {
                case "message.updated":
                    TrackMessageUpdated(payload);
                    break;
                case "message.part.updated":
                    if (payload.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Object)
                        TrackPart(GetString(part, "messageID"), GetString(part, "id"), MapEventPartToMessagePart(part));
                    break;
                case "message.part.delta":
                    TrackDelta(payload);
                    break;
            }
        }
    }

    private void TrackMessageUpdated(JsonElement payload)
    {
        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
            return;

        var messageId = GetString(info, "id");
        if (string.IsNullOrWhiteSpace(messageId))
            return;

        var index = _messages.FindIndex(m => m.Id == messageId);
        var role = GetString(info, "role") ?? "assistant";

        // A user echo carries the harness's own ID; the prompt is already recorded under Fleet's.
        if (index < 0 && role == "user")
            return;

        var message = index >= 0
            ? _messages[index]
            : new HarnessMessage { Id = messageId, Role = role, Parts = [], Timestamp = DateTimeOffset.UtcNow };

        message = message with
        {
            Agent = GetString(info, "agent") ?? message.Agent,
            ModelId = GetString(info, "modelID") ?? message.ModelId,
        };

        if (index >= 0)
        {
            _messages[index] = message;
        }
        else
        {
            _messages.Add(message);
            _partIds[messageId] = [];
        }

        if (payload.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
                TrackPart(messageId, GetString(part, "id"), MapEventPartToMessagePart(part));
        }
    }

    private void TrackPart(string? messageId, string? partId, MessagePart? part)
    {
        if (string.IsNullOrWhiteSpace(messageId) || part is null)
            return;

        var index = _messages.FindIndex(m => m.Id == messageId);
        if (index < 0)
            return;

        var message = _messages[index];
        var partIds = _partIds[messageId];
        var parts = message.Parts.ToList();
        var partIndex = partId is null ? -1 : partIds.IndexOf(partId);

        if (partIndex >= 0)
        {
            parts[partIndex] = part;
        }
        else
        {
            parts.Add(part);
            partIds.Add(partId);
        }

        _messages[index] = message with { Parts = parts };
    }

    private void TrackDelta(JsonElement payload)
    {
        if (GetString(payload, "field") != "text")
            return;

        var messageId = GetString(payload, "messageID");
        var partId = GetString(payload, "partID");
        var delta = GetString(payload, "delta");
        if (string.IsNullOrWhiteSpace(messageId) || delta is null)
            return;

        var index = _messages.FindIndex(m => m.Id == messageId);
        if (index < 0)
            return;

        var partIndex = partId is null ? -1 : _partIds[messageId].IndexOf(partId);
        var existing = partIndex >= 0 ? _messages[index].Parts[partIndex] as TextPart : null;
        TrackPart(messageId, partId, new TextPart((existing?.Text ?? "") + delta));
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private async Task TryHandleDelegationEventAsync(HarnessEvent evt)
    {
        if (_scopeFactory is null || string.IsNullOrWhiteSpace(_ownerUserId) || !evt.Payload.HasValue)
            return;

        if (!TryExtractTaskCompletion(evt.Payload.Value, out var toolCallId, out var status))
            return;

        using var scope = _scopeFactory.CreateScope();
        var delegationRepository = scope.ServiceProvider.GetService<IDelegationRepository>();
        var delegationService = scope.ServiceProvider.GetService<DelegationService>();
        if (delegationRepository is null || delegationService is null)
            return;

        var delegation = await delegationRepository.GetByParentToolCallIdAsync(
            _fleetSessionId,
            toolCallId).ConfigureAwait(false);

        if (delegation is null)
            return;

        await delegationService.HandleDelegationFinishedAsync(
            delegation.Id,
            status).ConfigureAwait(false);
    }

    private static bool TryExtractTaskCompletion(JsonElement payload, out string toolCallId, out string status)
    {
        toolCallId = string.Empty;
        status = string.Empty;

        if (!payload.TryGetProperty("part", out var part)
            || part.ValueKind != JsonValueKind.Object
            || !part.TryGetProperty("type", out var typeEl)
            || typeEl.GetString() != "tool"
            || !part.TryGetProperty("tool", out var toolEl)
            || toolEl.GetString() != "task")
        {
            return false;
        }

        if (!part.TryGetProperty("state", out var state)
            || state.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("status", out var statusEl))
        {
            return false;
        }

        status = statusEl.GetString() ?? string.Empty;
        if (status is not ("completed" or "error" or "cancelled"))
            return false;

        if (part.TryGetProperty("callID", out var callIdEl) || part.TryGetProperty("callId", out callIdEl))
            toolCallId = callIdEl.GetString() ?? string.Empty;

        return !string.IsNullOrWhiteSpace(toolCallId);
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

            // User messages are persisted at send time by the orchestrator with a
            // synthetic ID. The echoed message.updated from the harness carries a different
            // ID and no parts — creating a stub here would produce an empty duplicate.
            // Skip it; the orchestrator row already has the text.
            if (existing is null && role is "user")
                return false;

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
                },
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        var part = evt.Payload.Value.GetProperty("part");
        var durableMessageId = part.GetProperty("messageID").GetString();
        if (string.IsNullOrWhiteSpace(durableMessageId))
            return false;

        var existingMessage = await messageRepo.GetByIdAsync(durableMessageId, _fleetSessionId).ConfigureAwait(false);

        // Convert the raw event part into a typed MessagePart so the polymorphic
        // type discriminator is serialized correctly. System.Text.Json requires the
        // discriminator to precede other properties during deserialization, so we
        // cannot store the raw event JSON directly.
        var fleetPart = MapEventPartToMessagePart(part);

        // No existing row: defer to MessagePersistenceProjection. The OpenCode protocol carries
        // role on message.updated/created (in info.role), not on message.part.updated, so this
        // branch can only guess the role and previously defaulted to "assistant" — that
        // racing pre-write was the source of user messages ending up as role=assistant in the
        // beta-harness loop. The projection will create the row with the authoritative role
        // from the prior message.updated event; subsequent message.part.updated events arrive
        // through the same projection path and merge cleanly via MergePartAndMetadata.
        if (existingMessage is null)
            return true;

        PersistedMessage updated = fleetPart is not null
            ? MessagePersistenceService.MergePartAndMetadata(existingMessage, fleetPart, role: null, agentName: null)
            : existingMessage;

        await writer.WriteAsync(
            new SessionActivityWriteRequest
            {
                MessagesToUpsert = [updated],
            },
            CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Maps a raw event part <see cref="JsonElement"/> to a typed <see cref="MessagePart"/>.
    /// System.Text.Json polymorphic deserialization requires the type discriminator to be
    /// the first JSON property, so raw event payloads (which may have other properties first)
    /// must be converted to typed records before persistence.
    /// </summary>
    private static MessagePart? MapEventPartToMessagePart(JsonElement part)
    {
        if (!part.TryGetProperty("type", out var typeEl))
            return null;

        var partType = typeEl.GetString();
        return partType switch
        {
            "text" => new TextPart(
                part.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : ""),
            "tool" => new ToolUsePart(
                ToolCallId: part.TryGetProperty("callID", out var callIdEl) ? callIdEl.GetString() ?? "" : "",
                ToolName: part.TryGetProperty("tool", out var toolEl) ? toolEl.GetString() ?? "" : "",
                Arguments: part.TryGetProperty("input", out var inputEl)
                    ? inputEl.Clone()
                    : (part.TryGetProperty("state", out var stateEl2) && stateEl2.TryGetProperty("input", out var nestedInput)
                        ? nestedInput.Clone()
                        : JsonDocument.Parse("{}").RootElement.Clone()),
                State: MapToolState(part)),
            _ => null,
        };
    }

    private static ToolUseState MapToolState(JsonElement part)
    {
        if (!part.TryGetProperty("state", out var stateEl) || stateEl.ValueKind != JsonValueKind.Object)
            return ToolUseState.Running;

        if (!stateEl.TryGetProperty("status", out var statusEl))
            return ToolUseState.Running;

        return statusEl.GetString() switch
        {
            "completed" => ToolUseState.Completed,
            "error" => ToolUseState.Error,
            "pending" => ToolUseState.Pending,
            _ => ToolUseState.Running,
        };
    }

    /// <summary>
    /// Tracks question tool context from emitted events so that <see cref="AnswerQuestionAsync"/>
    /// can produce a proper completion event without manual setup.
    /// </summary>
    private void TryTrackQuestionContext(HarnessEvent evt)
    {
        if (evt.Type != "message.part.updated" || !evt.Payload.HasValue)
            return;

        var payload = evt.Payload.Value;
        if (!payload.TryGetProperty("part", out var part))
            return;
        if (!part.TryGetProperty("tool", out var toolEl) || toolEl.GetString() != "question")
            return;

        if (part.TryGetProperty("messageID", out var msgIdEl))
            _lastQuestionMessageId = msgIdEl.GetString();

        if (part.TryGetProperty("state", out var stateEl) && stateEl.TryGetProperty("input", out var inputEl))
            _lastQuestionInput = inputEl.Clone();
    }
}
