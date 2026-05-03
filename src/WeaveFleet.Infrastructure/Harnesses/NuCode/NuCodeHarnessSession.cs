using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using global::NuCode;
using global::NuCode.Agents;
using global::NuCode.Events;
using global::NuCode.Sessions;
using global::NuCode.Tools;
using WeaveFleet.Domain.Harnesses;

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
    private readonly Channel<HarnessEvent> _eventChannel;
    private readonly CancellationTokenSource _cts = new();

    private NuCodeSession? _nuCodeSession;
    private HarnessSessionStatus _status = HarnessSessionStatus.Idle;

    public NuCodeHarnessSession(
        string instanceId,
        string fleetSessionId,
        string workingDirectory,
        ServiceProvider nuCodeProvider,
        IChatClient chatClient,
        ILogger<NuCodeHarnessSession> logger)
    {
        InstanceId = instanceId;
        FleetSessionId = fleetSessionId;
        _workingDirectory = workingDirectory;
        _nuCodeProvider = nuCodeProvider;
        _chatClient = chatClient;
        _logger = logger;
        _eventChannel = Channel.CreateUnbounded<HarnessEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
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
                    _workingDirectory, text.Length > 50 ? text[..50] : text, ct);
            }

            // Create user message
            var agentName = options?.Agent ?? "build";
            var userMsg = new UserMessage(MessageId.New(), _nuCodeSession.Id, DateTimeOffset.UtcNow, agentName);
            await sessionService.UpsertMessageAsync(userMsg, ct);

            // Store user text as a part on the message
            var userTextPart = new global::NuCode.Sessions.TextPart(PartId.New(), _nuCodeSession.Id, userMsg.Id, text);
            await sessionService.UpsertPartAsync(userTextPart, ct);

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
            var messages = await sessionService.GetMessagesAsync(_nuCodeSession.Id, ct);
            var chatMessages = NuCodeMapper.ToChatMessages(messages);

            // Create assistant message placeholder
            var assistantMsg = new AssistantMessage(
                MessageId.New(), _nuCodeSession.Id, DateTimeOffset.UtcNow,
                userMsg.Id, agentName, "anthropic", "claude-sonnet-4-20250514");

            // Process the agent loop
            var result = await processor.ProcessAsync(agent, assistantMsg, chatMessages, agentSession, ct);

            // Continue looping if tools were called
            while (result == ProcessResult.Continue && !ct.IsCancellationRequested)
            {
                messages = await sessionService.GetMessagesAsync(_nuCodeSession.Id, ct);
                chatMessages = NuCodeMapper.ToChatMessages(messages);
                assistantMsg = new AssistantMessage(
                    MessageId.New(), _nuCodeSession.Id, DateTimeOffset.UtcNow,
                    userMsg.Id, agentName, "anthropic", "claude-sonnet-4-20250514");
                result = await processor.ProcessAsync(agent, assistantMsg, chatMessages, agentSession, ct);
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
        // Return the configured provider/model
        IReadOnlyList<ProviderInfo> providers =
        [
            new ProviderInfo
            {
                Id = "anthropic",
                Name = "Anthropic",
                Models = [new ModelInfo { Id = "claude-sonnet-4-20250514", Name = "Claude Sonnet 4" }],
            },
            new ProviderInfo
            {
                Id = "openai",
                Name = "OpenAI",
                Models = [new ModelInfo { Id = "gpt-4o", Name = "GPT-4o" }],
            },
        ];

        return Task.FromResult(providers);
    }

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

    private void SubscribeToNuCodeEvents(INuCodeEventBus eventBus)
    {
        eventBus.Subscribe(MessageEvents.Updated, e =>
        {
            var payload = JsonSerializer.SerializeToElement(
                new NuCodeMessageUpdatedPayload { MessageId = e.Properties.MessageId.Value },
                NuCodeJsonContext.Default.NuCodeMessageUpdatedPayload);

            _eventChannel.Writer.TryWrite(new HarnessEvent
            {
                Type = "message.updated",
                SessionId = FleetSessionId,
                FleetSessionId = FleetSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = payload,
            });
        });

        eventBus.Subscribe(MessageEvents.PartUpdated, e =>
        {
            var payload = JsonSerializer.SerializeToElement(
                new NuCodePartUpdatedPayload { MessageId = e.Properties.MessageId.Value, PartId = e.Properties.PartId.Value },
                NuCodeJsonContext.Default.NuCodePartUpdatedPayload);

            _eventChannel.Writer.TryWrite(new HarnessEvent
            {
                Type = "message.part.updated",
                SessionId = FleetSessionId,
                FleetSessionId = FleetSessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Payload = payload,
            });
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "NuCode agent loop failed for instance {InstanceId}")]
    private partial void LogAgentLoopFailed(Exception ex, string instanceId);
}
