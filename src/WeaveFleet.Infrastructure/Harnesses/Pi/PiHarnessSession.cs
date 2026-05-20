using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary>Pi RPC-backed harness session.</summary>
internal sealed class PiHarnessSession : IHarnessSession
{
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    private static readonly Action<ILogger, string, Exception?> LogEventPumpFaulted =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "EventPumpFaulted"),
            "Pi event pump faulted for {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogSendPrompt =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(2, "SendPrompt"),
            "Sending prompt to Pi instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogAbort =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(3, "Abort"),
            "Aborting Pi instance {InstanceId}.");

    private static readonly Action<ILogger, string, Exception?> LogStop =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4, "Stop"),
            "Stopping Pi instance {InstanceId}.");

    private static readonly Action<ILogger, string, int, Exception?> LogProcessExited =
        LoggerMessage.Define<string, int>(LogLevel.Error, new EventId(5, "ProcessExited"),
            "Pi instance {InstanceId} process exited unexpectedly with code {ExitCode}.");

    private static readonly Action<ILogger, string, Exception?> LogSessionDeleteFailed =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(6, "SessionDeleteFailed"),
            "Failed to delete Pi session file {SessionFile}.");

    private readonly IPiProcessManager _processManager;
    private readonly PiJsonlClient _client;
    private readonly PiMapper _mapper;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger<PiHarnessSession> _logger;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Channel<HarnessEvent> _eventChannel = Channel.CreateBounded<HarnessEvent>(new BoundedChannelOptions(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = true,
    });

    private readonly Task _pumpTask;
    private string? _sessionFile;
    private string? _sessionId;
    private HarnessSessionStatus _status = HarnessSessionStatus.Idle;
    private bool _disposed;

    public PiHarnessSession(
        string instanceId,
        string fleetSessionId,
        PiProcessManager processManager,
        PiJsonlClient client,
        TimeSpan shutdownTimeout,
        ILogger<PiHarnessSession> logger)
        : this(instanceId, fleetSessionId, (IPiProcessManager)processManager, client, shutdownTimeout, logger)
    {
    }

    internal PiHarnessSession(
        string instanceId,
        string fleetSessionId,
        IPiProcessManager processManager,
        PiJsonlClient client,
        TimeSpan shutdownTimeout,
        ILogger<PiHarnessSession> logger)
    {
        InstanceId = instanceId;
        _processManager = processManager;
        _client = client;
        _shutdownTimeout = shutdownTimeout;
        _logger = logger;
        _mapper = new PiMapper(fleetSessionId);

        _processManager.ProcessExited += OnProcessExited;
        _pumpTask = Task.Run(PumpEventsAsync, CancellationToken.None);
    }

    public string InstanceId { get; }

    public int? ProcessId => _processManager.ProcessId;

    public string? ResumeToken => BuildResumeToken();

    public string HarnessType => "pi";

    public HarnessSessionStatus Status => _status;

    internal void UpdateState(PiState state)
    {
        UpdateResumeState(state.SessionFile, state.SessionId);

        if (_status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped or HarnessSessionStatus.Error)
            return;

        _status = state.IsStreaming || state.IsCompacting || state.PendingMessageCount > 0
            ? HarnessSessionStatus.Running
            : HarnessSessionStatus.Idle;
    }

    public Task StopAsync(CancellationToken ct) => StopCoreAsync(ct);

    public async Task DeleteAsync(CancellationToken ct)
    {
        var sessionFile = _sessionFile;
        await StopCoreAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(sessionFile))
            TryDeleteLocalSessionFile(sessionFile);
    }

    public async Task SendPromptAsync(string text, PromptOptions? options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (options?.Attachments is { Count: > 0 })
            throw new NotSupportedException("The Pi harness does not support prompt attachments yet.");

        LogSendPrompt(_logger, InstanceId, null);
        var response = await _client.SendRequestAsync(new PiPromptCommand { Id = NewRequestId(), Message = text }, ct)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        _status = HarnessSessionStatus.Running;
    }

    public Task SendCommandAsync(CommandOptions options, CancellationToken ct)
        => throw new NotSupportedException("The Pi harness does not expose slash commands yet.");

    public async Task AbortAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        LogAbort(_logger, InstanceId, null);
        await _client.SendCommandAsync(new PiAbortCommand(), ct).ConfigureAwait(false);
        _status = HarnessSessionStatus.Idle;
    }

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<IReadOnlyList<string>> answers, CancellationToken ct)
        => throw new NotSupportedException("The Pi harness does not support the question tool in RPC mode.");

    public Task RejectQuestionAsync(string requestId, CancellationToken ct)
        => throw new NotSupportedException("The Pi harness does not support the question tool in RPC mode.");

    public async Task<MessagePage> GetMessagesAsync(MessageQuery? query, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var response = await _client.SendRequestAsync(new PiGetMessagesCommand { Id = NewRequestId() }, ct)
            .ConfigureAwait(false);
        EnsureSuccess(response);

        var messages = ExtractMessages(response.Data);
        if (!string.IsNullOrWhiteSpace(query?.Before))
        {
            var beforeIndex = messages.FindIndex(message => string.Equals(message.Id, query.Before, StringComparison.Ordinal));
            if (beforeIndex >= 0)
                messages = messages[..beforeIndex];
        }

        bool hasMore = false;
        if (query?.Limit is { } limit && limit >= 0 && messages.Count > limit)
        {
            hasMore = true;
            messages = messages.Take(limit).ToList();
        }

        if (TryGetHasMore(response.Data) is { } piHasMore)
            hasMore = piHasMore || hasMore;

        return new MessagePage(messages, hasMore);
    }

    public async IAsyncEnumerable<HarnessEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct)
    {
        if (!_processManager.IsRunning)
            return new HealthCheckResult(false, "Pi process is not running.");

        try
        {
            var response = await _client.SendRequestAsync(
                    new PiGetStateCommand { Id = NewRequestId() },
                    HealthCheckTimeout,
                    ct)
                .ConfigureAwait(false);

            if (!response.Success)
                return new HealthCheckResult(false, response.Error ?? "Pi get_state failed.");

            if (response.Data is { ValueKind: JsonValueKind.Object } data)
            {
                var state = JsonSerializer.Deserialize(data, PiJsonContext.Default.PiState);
                if (state is not null)
                    UpdateState(state);
            }

            return new HealthCheckResult(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(false, ex.Message);
        }
    }

    public Task<IReadOnlyList<AgentInfo>> GetAgentsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<AgentInfo>>([]);

    public Task<IReadOnlyList<CommandInfo>> GetCommandsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<CommandInfo>>([]);

    public Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderInfo>>([]);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _pumpCts.CancelAsync().ConfigureAwait(false);

        if (_status is not HarnessSessionStatus.Stopped and not HarnessSessionStatus.Error)
        {
            try
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort on dispose.
            }
        }

        _processManager.ProcessExited -= OnProcessExited;

        try
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during disposal.
            }

            await _processManager.DisposeAsync().ConfigureAwait(false);
            _eventChannel.Writer.TryComplete();
            _lifecycleLock.Dispose();
            _pumpCts.Dispose();
        }
    }

    private async Task StopCoreAsync(CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped)
                return;

            LogStop(_logger, InstanceId, null);
            _status = HarnessSessionStatus.Stopping;
            await _processManager.StopAsync(_shutdownTimeout).WaitAsync(ct).ConfigureAwait(false);
            _status = HarnessSessionStatus.Stopped;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task PumpEventsAsync()
    {
        try
        {
            await foreach (var evt in _client.Events.ReadAllAsync(_pumpCts.Token).ConfigureAwait(false))
            {
                UpdateSessionState(evt);

                foreach (var harnessEvent in _mapper.Map(evt))
                {
                    await _eventChannel.Writer.WriteAsync(harnessEvent, _pumpCts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during disposal or shutdown.
        }
        catch (Exception ex)
        {
            _status = HarnessSessionStatus.Error;
            LogEventPumpFaulted(_logger, InstanceId, ex);
            _eventChannel.Writer.TryComplete(ex);
            return;
        }
        finally
        {
            _eventChannel.Writer.TryComplete();
        }
    }

    private void UpdateSessionState(PiEvent evt)
    {
        switch (evt)
        {
            case PiAgentStartEvent or PiTurnStartEvent or PiCompactionStartEvent or PiAutoRetryStartEvent:
                SetRunningIfActive();
                break;
            case PiIdleEvent or PiAgentEndEvent or PiTurnEndEvent or PiCompactionEndEvent:
                SetIdleIfActive();
                break;
            case PiErrorEvent or PiProtocolErrorEvent:
                if (_status is not HarnessSessionStatus.Stopping and not HarnessSessionStatus.Stopped)
                    _status = HarnessSessionStatus.Error;
                break;
            case PiStateUpdateEvent { State: not null } stateUpdate:
                UpdateState(stateUpdate.State);
                break;
            case PiSessionSwitchedEvent switched:
                UpdateResumeState(switched.SessionFile, switched.SessionId);
                SetIdleIfActive();
                break;
        }
    }

    private void SetRunningIfActive()
    {
        if (_status is not HarnessSessionStatus.Stopping and not HarnessSessionStatus.Stopped and not HarnessSessionStatus.Error)
            _status = HarnessSessionStatus.Running;
    }

    private void SetIdleIfActive()
    {
        if (_status is not HarnessSessionStatus.Stopping and not HarnessSessionStatus.Stopped and not HarnessSessionStatus.Error)
            _status = HarnessSessionStatus.Idle;
    }

    private void UpdateResumeState(string? sessionFile, string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionFile))
            _sessionFile = sessionFile;

        if (!string.IsNullOrWhiteSpace(sessionId))
            _sessionId = sessionId;
    }

    private string? BuildResumeToken()
    {
        if (string.IsNullOrWhiteSpace(_sessionFile) && string.IsNullOrWhiteSpace(_sessionId))
            return null;

        return JsonSerializer.Serialize(new PiResumeToken
        {
            SessionFile = _sessionFile,
            SessionId = _sessionId,
        }, PiJsonContext.Default.PiResumeToken);
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        if (_status is HarnessSessionStatus.Stopping or HarnessSessionStatus.Stopped || _disposed)
        {
            _status = HarnessSessionStatus.Stopped;
            return;
        }

        LogProcessExited(_logger, InstanceId, exitCode, null);
        _status = HarnessSessionStatus.Error;
    }

    private static List<HarnessMessage> ExtractMessages(JsonElement? data)
    {
        if (data is null || data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];

        IReadOnlyList<PiMessage>? piMessages = null;
        if (data.Value.ValueKind == JsonValueKind.Array)
        {
            piMessages = JsonSerializer.Deserialize(data.Value, PiJsonContext.Default.PiMessageArray);
        }
        else if (data.Value.ValueKind == JsonValueKind.Object)
        {
            if (data.Value.TryGetProperty("messages", out var messagesElement)
                && messagesElement.ValueKind == JsonValueKind.Array)
            {
                piMessages = JsonSerializer.Deserialize(messagesElement, PiJsonContext.Default.PiMessageArray);
            }
            else
            {
                var response = JsonSerializer.Deserialize(data.Value, PiJsonContext.Default.PiGetMessagesResponse);
                piMessages = response?.Messages;
            }
        }

        if (piMessages is null)
            return [];

        var messages = new List<HarnessMessage>(piMessages.Count);
        foreach (var message in piMessages)
        {
            if (!string.IsNullOrWhiteSpace(message.Role))
                messages.Add(ToHarnessMessage(message));
        }

        return messages;
    }

    private static bool? TryGetHasMore(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("hasMore", out var hasMoreElement)
            || hasMoreElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return hasMoreElement.GetBoolean();
    }

    private static HarnessMessage ToHarnessMessage(PiMessage message)
    {
        return new HarnessMessage
        {
            Id = ResolveMessageId(message),
            Role = ToFleetRole(message.Role),
            Parts = MapMessageParts(message),
            Timestamp = message.Timestamp is { } timestamp
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : DateTimeOffset.UtcNow,
            Agent = message.Role.Equals("toolResult", StringComparison.OrdinalIgnoreCase)
                ? string.IsNullOrWhiteSpace(message.ToolName) ? "tool" : $"tool:{message.ToolName}"
                : null,
            ModelId = message.ResponseModel ?? message.Model,
        };
    }

    private static List<MessagePart> MapMessageParts(PiMessage message)
    {
        var parts = new List<MessagePart>(message.Content.Count + 1);
        foreach (var content in message.Content)
        {
            switch (content)
            {
                case PiTextContent text:
                    parts.Add(new TextPart(text.Text ?? string.Empty));
                    break;
                case PiThinkingContent thinking:
                    parts.Add(new ReasoningPart(thinking.Thinking ?? string.Empty));
                    break;
                case PiToolCallContent toolCall:
                    parts.Add(new ToolUsePart(
                        ToolCallId: toolCall.Id ?? string.Empty,
                        ToolName: toolCall.Name ?? string.Empty,
                        Arguments: toolCall.Arguments ?? TryParseJson(toolCall.PartialArgs) ?? default,
                        State: ToolUseState.Pending));
                    break;
            }
        }

        if (message.Role.Equals("toolResult", StringComparison.OrdinalIgnoreCase))
        {
            var content = string.Concat(message.Content.OfType<PiTextContent>().Select(static text => text.Text));
            if (!string.IsNullOrEmpty(content) || !string.IsNullOrWhiteSpace(message.ToolCallId))
                parts.Add(new ToolResultPart(message.ToolCallId ?? string.Empty, content, message.IsError ?? false));
        }

        return parts;
    }

    private static JsonElement? TryParseJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(raw, PiJsonContext.Default.String);
        }
    }

    private static string ResolveMessageId(PiMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.ResponseId))
            return message.ResponseId;

        if (message.Timestamp is { } timestamp)
            return $"pi-{ToFleetRole(message.Role)}-{timestamp}";

        return $"pi-{ToFleetRole(message.Role)}-{Guid.NewGuid():N}";
    }

    private static string ToFleetRole(string role)
        => role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant";

    private static void EnsureSuccess(PiResponseEvent response)
    {
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? $"Pi command failed: {response.Command}.");
    }

    private void TryDeleteLocalSessionFile(string sessionFile)
    {
        try
        {
            var fullPath = Path.GetFullPath(sessionFile);
            var sessionDirectory = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".pi",
                "agent",
                "sessions"));

            if (!fullPath.StartsWith(sessionDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(fullPath))
            {
                return;
            }

            File.Delete(fullPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSessionDeleteFailed(_logger, sessionFile, ex);
        }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");
}
