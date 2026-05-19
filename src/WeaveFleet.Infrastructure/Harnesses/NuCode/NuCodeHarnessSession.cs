using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using global::NuCode;
using global::NuCode.Agents;
using global::NuCode.Events;
using global::NuCode.Sessions;
using global::NuCode.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// <see cref="IHarnessSession"/> implementation that bridges an in-process NuCode agent session
/// to the WeaveFleet harness interface. Manages the agent lifecycle, event bridging, and message mapping.
/// </summary>
public sealed partial class NuCodeHarnessSession : IHarnessSession
{
    private readonly ServiceProvider _nuCodeProvider;
    private readonly IChatClient _chatClient;
    private readonly ILogger<NuCodeHarnessSession> _logger;
    private readonly string _workingDirectory;
    private readonly string _provider;
    private readonly string _modelId;
    private readonly string? _projectId;
    private readonly string? _projectName;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _ownerUserId;
    private readonly IAnalyticsCollector? _analyticsCollector;
    private readonly Channel<HarnessEvent> _eventChannel;
    private readonly CancellationTokenSource _cts = new();

    // Tracks in-flight task tool delegations: callId → delegationId
    private readonly ConcurrentDictionary<string, string> _pendingDelegations = new();

    private NuCodeSession? _nuCodeSession;
    private HarnessSessionStatus _status = HarnessSessionStatus.Idle;
    private volatile bool _subscribed;

    public NuCodeHarnessSession(
        string instanceId,
        string fleetSessionId,
        string workingDirectory,
        string provider,
        string modelId,
        string? projectId,
        string? projectName,
        string ownerUserId,
        IServiceScopeFactory scopeFactory,
        ServiceProvider nuCodeProvider,
        IChatClient chatClient,
        ILogger<NuCodeHarnessSession> logger,
        IAnalyticsCollector? analyticsCollector = null)
    {
        InstanceId = instanceId;
        FleetSessionId = fleetSessionId;
        _workingDirectory = workingDirectory;
        _provider = provider;
        _modelId = modelId;
        _projectId = projectId;
        _projectName = projectName;
        _ownerUserId = ownerUserId;
        _scopeFactory = scopeFactory;
        _nuCodeProvider = nuCodeProvider;
        _chatClient = chatClient;
        _logger = logger;
        _analyticsCollector = analyticsCollector;
        _eventChannel = Channel.CreateUnbounded<HarnessEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Fleet session ID for event routing.</summary>
    internal string FleetSessionId { get; }

    /// <inheritdoc />
    public string InstanceId { get; }

    /// <inheritdoc />
    public int? ProcessId => null; // In-process

    /// <inheritdoc />
    public string? ResumeToken => _nuCodeSession?.Id.Value;

    /// <inheritdoc />
    public string HarnessType => "nucode";

    /// <inheritdoc />
    public HarnessSessionStatus Status => _status;

    /// <inheritdoc />
    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        _status = HarnessSessionStatus.Running;
        EmitStatusEvent();

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            var linkedToken = linked.Token;

            using var scope = _nuCodeProvider.CreateScope();
            var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var profileRegistry = scope.ServiceProvider.GetRequiredService<IAgentProfileRegistry>();
            var toolRegistry = scope.ServiceProvider.GetRequiredService<IToolRegistry>();
            var agentFactory = scope.ServiceProvider.GetRequiredService<INuCodeAgentFactory>();
            var eventBus = scope.ServiceProvider.GetRequiredService<INuCodeEventBus>();

            // Subscribe to NuCode events and bridge to HarnessEvent channel
            SubscribeToNuCodeEvents(eventBus);

            // Create or reuse session
            if (_nuCodeSession is null)
            {
                _nuCodeSession = await sessionService.CreateSessionAsync(
                    _workingDirectory, text.Length > 50 ? text[..50] : text, linkedToken);

                EmitSessionCreatedEvent();
                EmitSessionUpdatedEvent(text.Length > 50 ? text[..50] : text);
            }

            // Create user message
            var agentName = options?.Agent ?? "build";
            var userMsg = new UserMessage(MessageId.New(), _nuCodeSession.Id, DateTimeOffset.UtcNow, agentName);
            await sessionService.UpsertMessageAsync(userMsg, linkedToken);

            // Store user text as a part on the message
            var userTextPart = new global::NuCode.Sessions.TextPart(PartId.New(), _nuCodeSession.Id, userMsg.Id, text);
            await sessionService.UpsertPartAsync(userTextPart, linkedToken);

            // Emit user message event
            EmitMessageEvent(userMsg, text);

            // Determine agent profile
            var profile = profileRegistry.Get(agentName) ?? profileRegistry.Get("build")!;

            // Get tools filtered for profile
            var tools = toolRegistry.GetForProfile(profile)
                .Select(t => (AITool)t.ToAIFunction())
                .ToList();

            // Create agent and process
            var agent = agentFactory.CreateAgent(profile, _chatClient, tools);
            var processor = scope.ServiceProvider.GetRequiredService<ISessionProcessor>();
            var agentSession = new NuCodeAgentSession(_nuCodeSession);

            // Build chat messages from session history
            var messages = await sessionService.GetMessagesAsync(_nuCodeSession.Id, linkedToken);
            var chatMessages = NuCodeMapper.ToChatMessages(messages);

            // Create assistant message placeholder
            var assistantMsg = new AssistantMessage(
                MessageId.New(), _nuCodeSession.Id, DateTimeOffset.UtcNow,
                userMsg.Id, agentName, _provider, _modelId);

            // Process the agent loop
            var result = await processor.ProcessAsync(agent, assistantMsg, chatMessages, agentSession, linkedToken);

            // Continue looping if tools were called
            while (result == ProcessResult.Continue && !linkedToken.IsCancellationRequested)
            {
                messages = await sessionService.GetMessagesAsync(_nuCodeSession.Id, linkedToken);
                chatMessages = NuCodeMapper.ToChatMessages(messages);
                assistantMsg = new AssistantMessage(
                    MessageId.New(), _nuCodeSession.Id, DateTimeOffset.UtcNow,
                    userMsg.Id, agentName, _provider, _modelId);
                result = await processor.ProcessAsync(agent, assistantMsg, chatMessages, agentSession, linkedToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation via AbortAsync
        }
        catch (Exception ex)
        {
            LogAgentLoopFailed(ex, InstanceId);
            _status = HarnessSessionStatus.Error;
            EmitStatusEvent();
            return;
        }

        _status = HarnessSessionStatus.Idle;
        EmitStatusEvent();
    }

    /// <inheritdoc />
    public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        // NuCode doesn't support slash commands
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AbortAsync(CancellationToken ct)
    {
        _cts.Cancel();
        _status = HarnessSessionStatus.Idle;
        EmitStatusEvent();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        if (_nuCodeSession is null)
        {
            return new MessagePage([], false);
        }

        using var scope = _nuCodeProvider.CreateScope();
        var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
        var messages = await sessionService.GetMessagesAsync(_nuCodeSession.Id, ct);

        var harnessMessages = NuCodeMapper.ToHarnessMessages(messages);

        // Apply query pagination
        if (query?.Limit is > 0)
        {
            return new MessagePage(harnessMessages.Take(query.Limit.Value).ToList(), false);
        }

        return new MessagePage(harnessMessages, false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct))
        {
            yield return evt;
        }
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
    {
        return Task.FromResult(new HealthCheckResult(true, null));
    }

    /// <inheritdoc />
    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
        => throw new NotSupportedException("The NuCode harness does not support the question tool.");

    /// <inheritdoc />
    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
        => throw new NotSupportedException("The NuCode harness does not support the question tool.");

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
    {
        using var scope = _nuCodeProvider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IAgentProfileRegistry>();
        var agents = registry.GetVisible()
            .Select(p => new AgentInfo
            {
                Name = p.Name,
                Description = p.Description,
                Mode = p.Mode.ToString().ToLowerInvariant(),
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentInfo>>(agents);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<CommandInfo>>([]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
    {
        IReadOnlyList<ProviderInfo> providers =
        [
            new ProviderInfo
            {
                Id = _provider,
                Name = ToDisplayName(_provider),
                Models = [new ModelInfo { Id = _modelId, Name = _modelId }],
            },
        ];

        return Task.FromResult(providers);
    }

    private static string ToDisplayName(string provider) => provider.ToLowerInvariant() switch
    {
        "anthropic" => "Anthropic",
        "openai" => "OpenAI",
        "copilot" => "GitHub Copilot",
        _ => provider,
    };

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        _cts.Cancel();
        _eventChannel.Writer.TryComplete();
        _status = HarnessSessionStatus.Stopped;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken ct)
    {
        return StopAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _cts.Dispose();
        await _nuCodeProvider.DisposeAsync();
    }

    private void EmitStatusEvent()
    {
        var evt = new HarnessEvent
        {
            Type = _status == HarnessSessionStatus.Running ? "session.busy" : "session.idle",
            SessionId = FleetSessionId,
            FleetSessionId = FleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
        };
        _eventChannel.Writer.TryWrite(evt);
    }

    private void EmitSessionCreatedEvent()
    {
        _eventChannel.Writer.TryWrite(new HarnessEvent
        {
            Type = EventTypes.SessionCreated,
            SessionId = FleetSessionId,
            FleetSessionId = FleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
        });
    }

    private void EmitSessionUpdatedEvent(string title)
    {
        var payload = JsonSerializer.SerializeToElement(
            new NuCodeSessionUpdatedPayload { Title = title },
            NuCodeJsonContext.Default.NuCodeSessionUpdatedPayload);

        _eventChannel.Writer.TryWrite(new HarnessEvent
        {
            Type = EventTypes.SessionUpdated,
            SessionId = FleetSessionId,
            FleetSessionId = FleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        });
    }

    private void EmitMessageEvent(UserMessage msg, string text)
    {
        var payload = JsonSerializer.SerializeToElement(
            new NuCodeMessageCreatedPayload { MessageId = msg.Id.Value, Role = "user", Content = text },
            NuCodeJsonContext.Default.NuCodeMessageCreatedPayload);

        _eventChannel.Writer.TryWrite(new HarnessEvent
        {
            Type = "message.created",
            SessionId = FleetSessionId,
            FleetSessionId = FleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        });
    }

    // Cache of nuCode session ID → fleet session ID for child sessions
    private readonly ConcurrentDictionary<string, string> _childFleetSessionIdCache = new();

    private void SubscribeToNuCodeEvents(INuCodeEventBus eventBus)
    {
        if (_subscribed) return;
        _subscribed = true;

        eventBus.Subscribe(MessageEvents.Updated, e =>
        {
            var nuCodeSessionId = e.Properties.SessionId.Value;
            var payload = JsonSerializer.SerializeToElement(
                new NuCodeMessageUpdatedPayload { MessageId = e.Properties.MessageId.Value },
                NuCodeJsonContext.Default.NuCodeMessageUpdatedPayload);

            _ = EmitRoutedEventAsync("message.updated", nuCodeSessionId, payload);
        });

        eventBus.Subscribe(MessageEvents.PartUpdated, e =>
        {
            var nuCodeSessionId = e.Properties.SessionId.Value;
            var payload = JsonSerializer.SerializeToElement(
                new NuCodePartUpdatedPayload { MessageId = e.Properties.MessageId.Value, PartId = e.Properties.PartId.Value },
                NuCodeJsonContext.Default.NuCodePartUpdatedPayload);

            _ = EmitRoutedEventAsync("message.part.updated", nuCodeSessionId, payload);
        });

        eventBus.Subscribe(MessageEvents.PartDeltaReceived, e =>
        {
            var nuCodeSessionId = e.Properties.SessionId.Value;
            var p = e.Properties;
            var payload = JsonSerializer.SerializeToElement(
                new NuCodePartDeltaPayload { MessageId = p.MessageId.Value, PartId = p.PartId.Value, Field = p.Field, Delta = p.Delta },
                NuCodeJsonContext.Default.NuCodePartDeltaPayload);

            _ = EmitRoutedEventAsync("message.part.delta", nuCodeSessionId, payload);
        });

        // Delegation: detect task tool calls and child session creation
        eventBus.Subscribe(ToolEvents.Started, e =>
        {
            if (!string.Equals(e.Properties.ToolName, "task", StringComparison.OrdinalIgnoreCase))
                return;

            var callId = e.Properties.CallId;
            if (callId is null) return;

            _ = TryHandleDelegationStartedAsync(callId, e.Properties.ToolName);
        });

        eventBus.Subscribe(ToolEvents.Completed, e =>
        {
            if (!string.Equals(e.Properties.ToolName, "task", StringComparison.OrdinalIgnoreCase))
                return;

            var callId = e.Properties.CallId;
            if (callId is null) return;

            _ = TryHandleDelegationFinishedAsync(callId, "completed");
        });

        eventBus.Subscribe(ToolEvents.Failed, e =>
        {
            if (!string.Equals(e.Properties.ToolName, "task", StringComparison.OrdinalIgnoreCase))
                return;

            var callId = e.Properties.CallId;
            if (callId is null) return;

            _ = TryHandleDelegationFinishedAsync(callId, "error");
        });

        eventBus.Subscribe(SessionEvents.Created, e =>
        {
            // Only handle child sessions (NuCode sets ParentId when TaskTool creates child sessions)
            // We ensure the child fleet session exists before child events arrive.
            _ = TryEnsureChildSessionAsync(e.Properties.SessionId.Value, e.Properties.Title);
        });

        // Analytics: collect token/cost data from StepFinishPart events on the parent session
        if (_analyticsCollector is not null)
        {
            eventBus.Subscribe(MessageEvents.PartUpdated, e =>
            {
                var nuCodeSessionId = e.Properties.SessionId.Value;
                if (_nuCodeSession is null ||
                    !string.Equals(nuCodeSessionId, _nuCodeSession.Id.Value, StringComparison.Ordinal))
                {
                    return; // Only collect tokens for the parent session
                }

                _ = TryCollectTokensAsync(e.Properties.PartId, e.Properties.SessionId);
            });
        }
    }

    /// <summary>
    /// Resolves the fleet session ID for a NuCode session ID and emits the event,
    /// routing child session events to their corresponding child fleet sessions.
    /// </summary>
    private async Task EmitRoutedEventAsync(string type, string nuCodeSessionId, JsonElement? payload = null)
    {
        var fleetSessionId = await ResolveFleetSessionIdAsync(nuCodeSessionId).ConfigureAwait(false);

        _eventChannel.Writer.TryWrite(new HarnessEvent
        {
            Type = type,
            SessionId = fleetSessionId ?? FleetSessionId,
            FleetSessionId = fleetSessionId,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = payload,
        });
    }

    /// <summary>
    /// Returns the fleet session ID for a given NuCode session ID.
    /// Returns null (routes to parent) for the parent session or if unresolvable.
    /// Caches resolved child session IDs.
    /// </summary>
    private async Task<string?> ResolveFleetSessionIdAsync(string nuCodeSessionId)
    {
        // Parent session — route to parent fleet session
        if (_nuCodeSession is not null &&
            string.Equals(nuCodeSessionId, _nuCodeSession.Id.Value, StringComparison.Ordinal))
        {
            return FleetSessionId;
        }

        // Check cache
        if (_childFleetSessionIdCache.TryGetValue(nuCodeSessionId, out var cachedId))
            return cachedId;

        // Look up in DB
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            var session = await repo.GetByHarnessIdAsync(nuCodeSessionId).ConfigureAwait(false);

            if (session is null ||
                !string.Equals(session.ParentSessionId, FleetSessionId, StringComparison.Ordinal))
            {
                return null;
            }

            _childFleetSessionIdCache[nuCodeSessionId] = session.Id;
            return session.Id;
        }
        catch (Exception ex)
        {
            LogDelegationFailed(ex, FleetSessionId);
            return null;
        }
    }

    private async Task TryHandleDelegationStartedAsync(string callId, string toolName)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var delegationService = scope.ServiceProvider.GetRequiredService<DelegationService>();
            var delegation = await delegationService.HandleDelegationDetectedAsync(
                FleetSessionId, callId, toolName).ConfigureAwait(false);
            _pendingDelegations[callId] = delegation.DelegationId;
        }
        catch (Exception ex)
        {
            LogDelegationFailed(ex, FleetSessionId);
        }
    }

    private async Task TryHandleDelegationFinishedAsync(string callId, string status)
    {
        try
        {
            if (!_pendingDelegations.TryGetValue(callId, out var delegationId))
                return;

            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var delegationService = scope.ServiceProvider.GetRequiredService<DelegationService>();
            await delegationService.HandleDelegationFinishedAsync(delegationId, status).ConfigureAwait(false);
            _pendingDelegations.TryRemove(callId, out _);
        }
        catch (Exception ex)
        {
            LogDelegationFailed(ex, FleetSessionId);
        }
    }

    private async Task TryEnsureChildSessionAsync(string nuCodeChildSessionId, string? title)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var sessionOrchestrator = scope.ServiceProvider.GetService<SessionOrchestrator>();
            if (sessionOrchestrator is null) return;

            await sessionOrchestrator.EnsureDelegatedChildSessionAsync(
                FleetSessionId,
                nuCodeChildSessionId,
                title ?? nuCodeChildSessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDelegationFailed(ex, FleetSessionId);
        }
    }

    private async Task TryCollectTokensAsync(PartId partId, SessionId sessionId)
    {
        try
        {
            using var scope = _nuCodeProvider.CreateScope();
            var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var messages = await sessionService.GetMessagesAsync(sessionId, CancellationToken.None).ConfigureAwait(false);

            global::NuCode.Sessions.StepFinishPart? stepPart = null;
            foreach (var msg in messages)
            {
                foreach (var part in msg.Parts)
                {
                    if (part is global::NuCode.Sessions.StepFinishPart sfp &&
                        string.Equals(sfp.Id.Value, partId.Value, StringComparison.Ordinal))
                    {
                        stepPart = sfp;
                        break;
                    }
                }
                if (stepPart is not null) break;
            }

            if (stepPart is null) return;

            var tokenData = new TokenEventData(
                EventId: Guid.NewGuid().ToString(),
                SessionId: FleetSessionId,
                ProjectId: _projectId,
                ProjectName: _projectName,
                WorkspaceDirectory: _workingDirectory,
                ModelId: _modelId,
                ProviderId: _provider,
                TokensInput: stepPart.Tokens.Input,
                TokensOutput: stepPart.Tokens.Output,
                TokensReasoning: stepPart.Tokens.Reasoning,
                TokensCacheRead: stepPart.Tokens.Cache.Read,
                TokensCacheWrite: stepPart.Tokens.Cache.Write,
                TokensTotal: stepPart.Tokens.Total ?? stepPart.Tokens.Input + stepPart.Tokens.Output,
                Cost: (double)stepPart.Cost,
                EstimatedCost: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UserId: _ownerUserId);

            _analyticsCollector!.AcceptTokenEvent(tokenData);
        }
        catch (Exception ex)
        {
            LogTokenCollectionFailed(ex, FleetSessionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "NuCode agent loop failed for instance {InstanceId}")]
    private partial void LogAgentLoopFailed(Exception ex, string instanceId);
    [LoggerMessage(Level = LogLevel.Warning, Message = "NuCode token collection failed for session {SessionId}")]
    private partial void LogTokenCollectionFailed(Exception ex, string sessionId);
    [LoggerMessage(Level = LogLevel.Warning, Message = "NuCode delegation handling failed for session {SessionId}")]
    private partial void LogDelegationFailed(Exception ex, string sessionId);
}
