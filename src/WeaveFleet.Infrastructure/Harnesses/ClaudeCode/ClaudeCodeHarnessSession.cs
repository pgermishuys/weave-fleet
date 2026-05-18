using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Wraps a Claude Code CLI session for a single Fleet session.
/// Implements <see cref="IHarnessSession"/> with full database persistence.
/// Each instance owns its own message persistence — the relay is not involved.
/// </summary>
internal sealed class ClaudeCodeHarnessSession : IHarnessSession
{
    private static readonly Action<ILogger, string, Exception?> LogSendPrompt =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "SendPrompt"),
            "Sending prompt to ClaudeCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogAbort =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "Abort"),
            "Aborting ClaudeCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogStop =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(3, "Stop"),
            "Stopping ClaudeCode instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogPersistFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "PersistFailed"),
            "Failed to persist message for ClaudeCode session {SessionId}.");

    private static readonly Action<ILogger, string, Exception?> LogSessionId =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(5, "SessionId"),
            "ClaudeCode session ID: {SessionId}.");

    private readonly string _workingDirectory;
    private readonly ClaudeCodeOptions _config;
    private readonly IReadOnlyDictionary<string, string> _environmentVariables;
    private readonly TimeSpan _shutdownTimeout;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _fleetSessionId;
    private readonly string _ownerUserId;
    private readonly ILogger<ClaudeCodeHarnessSession> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAnalyticsCollector? _analyticsCollector;
    private readonly string? _projectId;
    private readonly string? _projectName;

    // Bounded channel for event delivery (DropOldest so slow consumers don't stall the pump)
    private readonly Channel<HarnessEvent> _eventChannel =
        Channel.CreateBounded<HarnessEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false,
        });

    private readonly SemaphoreSlim _promptLock = new(1, 1);

    private string? _claudeSessionId;    // captured from init message, used for --resume
    private string? _modelId;             // captured from init or result messages
    private HarnessSessionStatus _status = HarnessSessionStatus.Idle;
    private ClaudeCodeProcessManager? _activeProcess;
    private bool _disposed;

    /// <summary>Initialises the instance with all required dependencies.</summary>
    public ClaudeCodeHarnessSession(
        string instanceId,
        string fleetSessionId,
        string workingDirectory,
        ClaudeCodeOptions config,
        IReadOnlyDictionary<string, string> environmentVariables,
        TimeSpan shutdownTimeout,
        IServiceScopeFactory scopeFactory,
        ILogger<ClaudeCodeHarnessSession> logger,
        ILoggerFactory loggerFactory,
        string ownerUserId,
        IAnalyticsCollector? analyticsCollector = null,
        string? projectId = null,
        string? projectName = null,
        string? claudeSessionId = null)
    {
        InstanceId = instanceId;
        _fleetSessionId = fleetSessionId;
        _workingDirectory = workingDirectory;
        _config = config;
        _environmentVariables = environmentVariables;
        _shutdownTimeout = shutdownTimeout;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _ownerUserId = ownerUserId;
        _analyticsCollector = analyticsCollector;
        _projectId = projectId;
        _projectName = projectName;
        _claudeSessionId = claudeSessionId;
    }

    /// <inheritdoc />
    public string InstanceId { get; }

    /// <inheritdoc />
    public int? ProcessId => _activeProcess?.ProcessId;

    /// <inheritdoc />
    public string HarnessType => "claude-code";

    /// <inheritdoc />
    public string? ResumeToken => _claudeSessionId;

    /// <inheritdoc />
    public HarnessSessionStatus Status => _status;

    // -----------------------------------------------------------------------
    // IHarnessSession
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _promptLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LogSendPrompt(_logger, InstanceId, null);

            // 2. Emit session busy status
            var busyEvent = ClaudeCodeMapper.CreateSessionStatusEvent(_fleetSessionId, "busy");
            await _eventChannel.Writer.WriteAsync(busyEvent, ct).ConfigureAwait(false);

            // 3. Spawn claude process
            var processManager = new ClaudeCodeProcessManager(
                _loggerFactory.CreateLogger<ClaudeCodeProcessManager>());

            var procOptions = new ClaudeCodeProcessOptions
            {
                BinaryPath = _config.BinaryPath,
                WorkingDirectory = _workingDirectory,
                Prompt = text,
                SessionId = _claudeSessionId,  // null for first prompt
                Model = options?.ModelId ?? _config.DefaultModel,
                PermissionMode = _config.PermissionMode,
                AllowedTools = _config.AllowedTools,
                MaxTurns = _config.MaxTurns,
                MaxBudgetUsd = _config.MaxBudgetUsd,
                ProcessTimeout = TimeSpan.FromSeconds(_config.ProcessTimeoutSeconds),
                EnvironmentVariables = _environmentVariables,
            };

            var stdout = await processManager.StartAsync(procOptions, ct).ConfigureAwait(false);

            _activeProcess = processManager;
            _status = HarnessSessionStatus.Running;

            // 4. Background pump — fire-and-forget (matches OpenCode pattern)
            _ = Task.Run(() => PumpStdoutAsync(stdout, processManager), CancellationToken.None);
        }
        finally
        {
            _promptLock.Release();
        }
    }

    /// <inheritdoc />
    public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        // Sanitize arguments: collapse newlines to spaces to prevent prompt injection
        var promptText = CommandFormatting.FormatCommandPrompt(options);

        var promptOptions = options.Agent is not null || options.ModelId is not null
            ? new PromptOptions { Agent = options.Agent, ModelId = options.ModelId }
            : null;

        return SendPromptAsync(promptText, promptOptions, ct);
    }

    /// <inheritdoc />
    public async Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        // Claude Code has no "get messages" API — always read from database
        using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

        var persisted = await repo.GetBySessionAsync(
            _fleetSessionId,
            query?.Limit ?? 50,
            query?.Before).ConfigureAwait(false);

        var messages = MessagePersistenceService.ToHarnessMessages(persisted);
        bool hasMore = query?.Limit.HasValue == true && persisted.Count >= query.Limit.Value;

        return new MessagePage(messages, hasMore);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    /// <inheritdoc />
    public async Task AbortAsync(CancellationToken ct)
    {
        LogAbort(_logger, InstanceId, null);

        if (_activeProcess is { IsRunning: true })
        {
            await _activeProcess.StopAsync(_shutdownTimeout).ConfigureAwait(false);
        }

        _status = HarnessSessionStatus.Idle;
    }

    /// <inheritdoc />
    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
        => throw new NotSupportedException("The ClaudeCode harness does not support the question tool.");

    /// <inheritdoc />
    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
        => throw new NotSupportedException("The ClaudeCode harness does not support the question tool.");

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
    {
        if (_activeProcess?.IsRunning == true)
        {
            return Task.FromResult(new HealthCheckResult(true, "Prompt active."));
        }

        return _status is HarnessSessionStatus.Idle or HarnessSessionStatus.Stopping
            ? Task.FromResult(new HealthCheckResult(true, null))
            : Task.FromResult(new HealthCheckResult(false, $"Unexpected status: {_status}"));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderInfo>>([]);

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct)
    {
        LogStop(_logger, InstanceId, null);
        _status = HarnessSessionStatus.Stopping;

        if (_activeProcess is not null)
        {
            await _activeProcess.StopAsync(_shutdownTimeout).ConfigureAwait(false);
        }

        _eventChannel.Writer.TryComplete();
        _status = HarnessSessionStatus.Stopped;
    }

    /// <inheritdoc />
    public Task DeleteAsync(CancellationToken ct) => StopAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_status is not HarnessSessionStatus.Stopped and not HarnessSessionStatus.Error)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort on dispose
            }
        }

        if (_activeProcess is not null)
        {
            await _activeProcess.DisposeAsync().ConfigureAwait(false);
        }

        _promptLock.Dispose();
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task PumpStdoutAsync(StreamReader stdout, ClaudeCodeProcessManager processManager)
    {
        try
        {
            await foreach (var msg in ClaudeCodeStdioClient
                .ReadMessagesAsync(stdout, _logger, CancellationToken.None)
                .ConfigureAwait(false))
            {
                // Capture session ID from init message
                if (msg is ClaudeCodeSystemMessage { Subtype: "init" } init)
                {
                    if (init.SessionId is not null)
                    {
                        _claudeSessionId = init.SessionId;
                        LogSessionId(_logger, init.SessionId, null);
                        // Persist resume token for session recovery
                        _ = PersistResumeTokenAsync(init.SessionId);
                    }

                    if (init.Model is not null)
                        _modelId = init.Model;
                }
                else if (msg is ClaudeCodeAssistantMessage assistantMsg)
                {
                    // Persist assistant message
                    var harnessMsg = ClaudeCodeMapper.ToHarnessMessage(
                        assistantMsg, DateTimeOffset.UtcNow);
                    await PersistMessageAsync(harnessMsg).ConfigureAwait(false);

                    if (assistantMsg.Message?.Model is not null)
                        _modelId = assistantMsg.Message.Model;
                }
                else if (msg is ClaudeCodeResultMessage result)
                {
                    // Extract analytics
                    if (_analyticsCollector is not null)
                    {
                        var tokenEvent = ClaudeCodeMapper.TryExtractTokenEvent(
                            result,
                            _fleetSessionId,
                            _projectId,
                            _projectName,
                            _workingDirectory,
                            _modelId,
                            _ownerUserId);
                        if (tokenEvent is not null)
                            _analyticsCollector.AcceptTokenEvent(tokenEvent);
                    }

                    if (result.SessionId is not null && _claudeSessionId is null)
                    {
                        _claudeSessionId = result.SessionId;
                        // Persist resume token captured from result message (fallback)
                        _ = PersistResumeTokenAsync(result.SessionId);
                    }

                    _status = HarnessSessionStatus.Idle;
                }
                if (msg is ClaudeCodeResultMessage)
                {
                    var events = ClaudeCodeMapper.ToFrontendEvents(msg, _fleetSessionId);
                    foreach (var evt in events)
                        await _eventChannel.Writer.WriteAsync(evt, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status = HarnessSessionStatus.Error;
        }
        finally
        {
            _status = _status is HarnessSessionStatus.Running
                ? HarnessSessionStatus.Idle
                : _status;

            await processManager.DisposeAsync().ConfigureAwait(false);

            if (ReferenceEquals(_activeProcess, processManager))
                _activeProcess = null;
        }
    }

    private async Task PersistMessageAsync(HarnessMessage message)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var sessionActivityWriteService = scope.ServiceProvider.GetRequiredService<SessionActivityWriteService>();
            var persisted = MessagePersistenceService.ToPersistedMessage(_fleetSessionId, message);
            var outboxMessages = new List<OutboxMessage>();
            var createdAt = DateTimeOffset.UtcNow.ToString("O");

            var messageUpdatedEvent = ClaudeCodeMapper.CreateMessageUpdatedEvent(message, _fleetSessionId);
            outboxMessages.Add(CreateOutboxMessage(messageUpdatedEvent, createdAt));

            for (int i = 0; i < message.Parts.Count; i++)
            {
                var partEvent = ClaudeCodeMapper.CreatePartUpdatedEvent(message.Id, _fleetSessionId, message.Parts[i], i);
                if (partEvent is not null)
                    outboxMessages.Add(CreateOutboxMessage(partEvent, createdAt));
            }

            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    MessagesToUpsert = [persisted],
                    OutboxMessages = outboxMessages
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Silent failure — persistence must never crash the instance
            LogPersistFailed(_logger, _fleetSessionId, ex);
        }
    }

    private async Task PersistResumeTokenAsync(string token)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
            await repo.UpdateResumeTokenAsync(_fleetSessionId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPersistFailed(_logger, _fleetSessionId, ex);
        }
    }

    private OutboxMessage CreateOutboxMessage(HarnessEvent harnessEvent, string createdAt)
    {
        return new OutboxMessage
        {
            Topic = $"session:{_fleetSessionId}",
            Type = harnessEvent.Type,
            Payload = harnessEvent.Payload!.Value.GetRawText(),
            UserId = _ownerUserId,
            CreatedAt = createdAt,
            AvailableAt = createdAt
        };
    }
}
