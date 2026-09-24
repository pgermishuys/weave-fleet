using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;
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

    private static readonly Action<ILogger, string, Exception?> LogPumpFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(6, "PumpFailed"),
            "Reading output from ClaudeCode instance {InstanceId} failed.");

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

    // The running prompt's assistant messages by Claude's message id, and the message each tool call is in.
    // Claude Code streams one content block per line and sends tool results as separate user lines,
    // so a message is built up across lines before it is saved. Saved messages get Fleet's ascending
    // ids: Claude's own ("msg_011C…") don't sort by time, and the conversation is ordered by id.
    private readonly Dictionary<string, HarnessMessage> _turnMessages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolCallMessageIds = new(StringComparer.Ordinal);

    private string? _claudeSessionId;    // captured from init message, used for --resume
    private string? _modelId;             // captured from init or result messages
    private HarnessSessionStatus _status = HarnessSessionStatus.Idle;
    private ClaudeCodeProcessManager? _activeProcess;
    private Task _activeTurn = Task.CompletedTask;
    private volatile bool _aborting;
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

            // 1. One claude process per session: a prompt sent mid-turn waits for that turn to end,
            //    so both don't resume the same Claude session at once.
            await _activeTurn.WaitAsync(ct).ConfigureAwait(false);
            _aborting = false;
            _turnMessages.Clear();
            _toolCallMessageIds.Clear();

            // The conversation is read back from Fleet's database, so the prompt is saved there, under
            // the id Fleet already showed it with.
            await PersistUserPromptAsync(text, options).ConfigureAwait(false);

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
                ProcessTimeout = _config.ProcessTimeoutSeconds is > 0 and var seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : null,
                EnvironmentVariables = _environmentVariables,
            };

            StreamReader stdout;
            try
            {
                stdout = await processManager.StartAsync(procOptions, ct).ConfigureAwait(false);
            }
            catch
            {
                await processManager.DisposeAsync().ConfigureAwait(false);
                await _eventChannel.Writer.WriteAsync(ClaudeCodeMapper.CreateSessionIdleEvent(_fleetSessionId), CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }

            _activeProcess = processManager;
            _status = HarnessSessionStatus.Running;

            // 4. Background pump — fire-and-forget (matches OpenCode pattern)
            _activeTurn = Task.Run(() => PumpStdoutAsync(stdout, processManager), CancellationToken.None);
        }
        finally
        {
            _promptLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> SendCommandAsync(CommandOptions options, CancellationToken ct)
    {
        // Sanitize arguments: collapse newlines to spaces to prevent prompt injection
        var promptText = CommandFormatting.FormatCommandPrompt(options);

        // Claude Code expands the command itself; the prompt Fleet keeps is "/name arguments", under Fleet's id.
        var promptOptions = new PromptOptions { Agent = options.Agent, ModelId = options.ModelId, MessageId = options.MessageId };

        await SendPromptAsync(promptText, promptOptions, ct).ConfigureAwait(false);
        return options.MessageId;
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
            _aborting = true;
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
    public Task<string?> AskOffTheRecordAsync(string prompt, CancellationToken ct)
        => Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<string?> GetActivityStatusAsync(CancellationToken ct)
    {
        // ClaudeCode doesn't have a status query endpoint, so we return null
        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc />
    public Task WaitForEventSubscriptionAsync(CancellationToken ct)
    {
        // ClaudeCode uses stdio streams which are synchronously available when the process starts.
        // No async subscription establishment is needed.
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
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

    internal async Task PumpStdoutAsync(StreamReader stdout, ClaudeCodeProcessManager processManager)
    {
        var sawResult = false;
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
                // Lines with a parent_tool_use_id are a sub-agent's own steps; the conversation shows
                // the call that started the sub-agent, not what it did.
                else if (msg is ClaudeCodeAssistantMessage { ParentToolUseId: null } assistantMsg)
                {
                    await AddAssistantBlocksAsync(assistantMsg).ConfigureAwait(false);

                    if (assistantMsg.Message?.Model is not null)
                        _modelId = assistantMsg.Message.Model;
                }
                else if (msg is ClaudeCodeUserMessage { ParentToolUseId: null } userMsg)
                {
                    await ApplyToolResultsAsync(userMsg).ConfigureAwait(false);
                }
                else if (msg is ClaudeCodeResultMessage result)
                {
                    sawResult = true;

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

                    // Claude Code writes some failures as an assistant message already, e.g. "Not logged in".
                    var failure = ClaudeCodeMapper.DescribeFailedResult(result);
                    if (failure is not null && !TurnEndsWithText(failure))
                        await PersistNoticeAsync($"Claude Code stopped: {failure}").ConfigureAwait(false);

                    _status = HarnessSessionStatus.Idle;

                    var events = ClaudeCodeMapper.ToFrontendEvents(msg, _fleetSessionId);
                    foreach (var evt in events)
                        await _eventChannel.Writer.WriteAsync(evt, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPumpFailed(_logger, InstanceId, ex);
        }
        finally
        {
            // A run that ends without a result line crashed or was killed. Unless the user stopped it,
            // say so in the conversation: otherwise the prompt just goes unanswered.
            var stopping = _status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped;
            if (!sawResult)
            {
                await FailUnfinishedToolsAsync().ConfigureAwait(false);

                if (!_aborting && !stopping)
                {
                    var exitCode = await processManager.WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await PersistNoticeAsync(DescribeEarlyExit(processManager, exitCode)).ConfigureAwait(false);
                }
            }

            await processManager.DisposeAsync().ConfigureAwait(false);

            if (ReferenceEquals(_activeProcess, processManager))
                _activeProcess = null;

            if (!stopping)
            {
                _status = HarnessSessionStatus.Idle;

                // Without a result line nothing has said the turn is over.
                if (!sawResult)
                    _eventChannel.Writer.TryWrite(ClaudeCodeMapper.CreateSessionIdleEvent(_fleetSessionId));
            }
        }
    }

    /// <summary>Adds an assistant line's content blocks to its message and saves it.</summary>
    private async Task AddAssistantBlocksAsync(ClaudeCodeAssistantMessage assistantMsg)
    {
        var incoming = ClaudeCodeMapper.ToHarnessMessage(assistantMsg, DateTimeOffset.UtcNow);
        if (incoming.Parts.Count == 0)
            return;

        var message = _turnMessages.TryGetValue(incoming.Id, out var existing)
            ? existing
            : incoming with { Id = AscendingMessageId.New(), Parts = [] };

        var parts = message.Parts.ToList();
        var partEvents = new List<HarnessEvent?>(incoming.Parts.Count);
        foreach (var part in incoming.Parts)
        {
            // Part ids are fixed when a part is first seen, so a reloaded session and later live
            // updates (a tool's result) name the same part.
            var index = parts.Count;
            var partId = $"{message.Id}-part-{index}";
            MessagePart stamped = part switch
            {
                TextPart text => text with { PartId = partId },
                ReasoningPart reasoning => reasoning with { PartId = partId },
                ToolUsePart tool => tool with { PartId = partId },
                _ => part,
            };

            if (stamped is ToolUsePart toolUse)
                _toolCallMessageIds[toolUse.ToolCallId] = incoming.Id;

            parts.Add(stamped);
            partEvents.Add(ClaudeCodeMapper.CreatePartUpdatedEvent(message.Id, _fleetSessionId, stamped, index));
        }

        message = message with { Parts = parts, ModelId = message.ModelId ?? incoming.ModelId };
        _turnMessages[incoming.Id] = message;
        await PersistMessageAsync(message, partEvents).ConfigureAwait(false);
    }

    /// <summary>Marks the tool calls a user line answers as finished, with what they returned.</summary>
    private async Task ApplyToolResultsAsync(ClaudeCodeUserMessage userMsg)
    {
        foreach (var block in userMsg.Message?.Content ?? [])
        {
            if (block is not ClaudeCodeToolResultBlock { ToolUseId: { } callId } result
                || !_toolCallMessageIds.TryGetValue(callId, out var claudeMessageId)
                || !_turnMessages.TryGetValue(claudeMessageId, out var message))
            {
                continue;
            }

            var parts = message.Parts.ToList();
            var index = parts.FindIndex(part => part is ToolUsePart tool && tool.ToolCallId == callId);
            if (index < 0)
                continue;

            var content = result.Content ?? string.Empty;
            var isError = result.IsError == true;
            var finished = (ToolUsePart)parts[index] with
            {
                State = isError ? ToolUseState.Error : ToolUseState.Completed,
                Error = isError ? content : null,
            };
            parts[index] = finished;
            parts.Add(new ToolResultPart(callId, content, isError));

            message = message with { Parts = parts };
            _turnMessages[claudeMessageId] = message;
            await PersistMessageAsync(
                message,
                [ClaudeCodeMapper.CreatePartUpdatedEvent(message.Id, _fleetSessionId, finished, index, isError ? null : content)])
                .ConfigureAwait(false);
        }
    }

    /// <summary>Marks tool calls that never got a result as failed, so they don't show as running forever.</summary>
    private async Task FailUnfinishedToolsAsync()
    {
        foreach (var (claudeMessageId, message) in _turnMessages.ToList())
        {
            var parts = message.Parts.ToList();
            var partEvents = new List<HarnessEvent?>();
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts[i] is not ToolUsePart { State: ToolUseState.Pending or ToolUseState.Running } tool)
                    continue;

                var stopped = tool with { State = ToolUseState.Error, Error = "Stopped before it finished." };
                parts[i] = stopped;
                partEvents.Add(ClaudeCodeMapper.CreatePartUpdatedEvent(message.Id, _fleetSessionId, stopped, i));
            }

            if (partEvents.Count == 0)
                continue;

            var updated = message with { Parts = parts };
            _turnMessages[claudeMessageId] = updated;
            await PersistMessageAsync(updated, partEvents).ConfigureAwait(false);
        }
    }

    private bool TurnEndsWithText(string text)
        => _turnMessages.Values.LastOrDefault()?.Parts.OfType<TextPart>().LastOrDefault()?.Text.Trim() == text;

    private string DescribeEarlyExit(ClaudeCodeProcessManager processManager, int? exitCode)
    {
        if (processManager.TimedOut)
        {
            return $"Claude Code was stopped after {_config.ProcessTimeoutSeconds} seconds, "
                + "the limit set by Fleet:ClaudeCode:ProcessTimeoutSeconds.";
        }

        var text = exitCode is { } code
            ? $"Claude Code exited with code {code} before finishing."
            : "Claude Code stopped before finishing.";

        var stderr = processManager.StderrTail;
        return stderr.Count > 0
            ? $"{text}\n\n```\n{string.Join("\n", stderr.TakeLast(3))}\n```"
            : text;
    }

    /// <summary>Saves a note from Fleet, such as why a run failed, as an assistant message.</summary>
    private Task PersistNoticeAsync(string text)
    {
        var id = AscendingMessageId.New();
        var part = new TextPart(text) { PartId = $"{id}-part-0" };
        var message = new HarnessMessage
        {
            Id = id,
            Role = "assistant",
            Parts = [part],
            Timestamp = DateTimeOffset.UtcNow,
        };

        return PersistMessageAsync(message, [ClaudeCodeMapper.CreatePartUpdatedEvent(id, _fleetSessionId, part, 0)]);
    }

    /// <summary>Saves the whole message and publishes the parts that changed.</summary>
    private async Task PersistMessageAsync(HarnessMessage message, IReadOnlyList<HarnessEvent?> partEvents)
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

            foreach (var partEvent in partEvents)
            {
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

    private async Task PersistUserPromptAsync(string text, PromptOptions? options)
    {
        try
        {
            using var userScope = BackgroundUserContext.BeginScope(_ownerUserId);
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var message = MessagePersistenceService.CreateUserPromptMessage(
                text,
                DateTimeOffset.UtcNow,
                options?.Agent,
                options?.MessageId ?? AscendingMessageId.New(),
                options?.Attachments);
            await repo.UpsertAsync(MessagePersistenceService.ToPersistedMessage(_fleetSessionId, message)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
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
