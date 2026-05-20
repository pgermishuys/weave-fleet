using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.Pi;

/// <summary>
/// JSONL transport for <c>pi --mode rpc</c>. Commands are written to stdin as one
/// JSON object per line; stdout lines are deserialized as Pi events.
/// </summary>
internal sealed class PiJsonlClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly Action<ILogger, string, string?, Exception?> LogCommandSent =
        LoggerMessage.Define<string, string?>(LogLevel.Debug, new EventId(1, "CommandSent"),
            "pi stdin: sent command type={CommandType} id={CommandId}");

    private static readonly Action<ILogger, string, Exception?> LogEventReceived =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(2, "EventReceived"),
            "pi stdout: received event type={EventType}");

    private static readonly Action<ILogger, string, string, bool, Exception?> LogResponseCorrelated =
        LoggerMessage.Define<string, string, bool>(LogLevel.Debug, new EventId(3, "ResponseCorrelated"),
            "pi stdout: correlated response command={CommandType} id={CommandId} success={Success}");

    private static readonly Action<ILogger, string, string, bool, Exception?> LogResponseCorrelatedByCommand =
        LoggerMessage.Define<string, string, bool>(LogLevel.Debug, new EventId(8, "ResponseCorrelatedByCommand"),
            "pi stdout: correlated response without id by command={CommandType} matchedId={CommandId} success={Success}");

    private static readonly Action<ILogger, string, string?, string?, Exception?> LogProtocolError =
        LoggerMessage.Define<string, string?, string?>(LogLevel.Warning, new EventId(4, "ProtocolError"),
            "pi protocol error kind={Kind} eventType={EventType} id={CommandId}");

    private static readonly Action<ILogger, string, string?, string?, string, Exception?> LogProtocolErrorRaw =
        LoggerMessage.Define<string, string?, string?, string>(LogLevel.Debug, new EventId(7, "ProtocolErrorRaw"),
            "pi protocol error raw kind={Kind} eventType={EventType} id={CommandId} raw={RawLine}");

    private static readonly Action<ILogger, Exception?> LogReadLoopFaulted =
        LoggerMessage.Define(LogLevel.Warning, new EventId(5, "ReadLoopFaulted"),
            "pi stdout reader loop faulted.");

    private static readonly Action<ILogger, Exception?> LogReadLoopCompleted =
        LoggerMessage.Define(LogLevel.Debug, new EventId(6, "ReadLoopCompleted"),
            "pi stdout reader loop completed.");

    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly ILogger<PiJsonlClient> _logger;
    private readonly Channel<PiEvent> _events = Channel.CreateUnbounded<PiEvent>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = true,
        AllowSynchronousContinuations = false,
    });
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerTask;
    private bool _disposed;

    public PiJsonlClient(PiProcessConnection connection, ILogger<PiJsonlClient> logger)
        : this(connection.StandardInput, connection.StandardOutput, logger)
    {
    }

    public PiJsonlClient(StreamWriter stdin, StreamReader stdout, ILogger<PiJsonlClient> logger)
    {
        _stdin = stdin;
        _stdout = stdout;
        _logger = logger;
        _readerTask = Task.Run(ReadLoopAsync);
    }

    /// <summary>Stream of Pi events and protocol-error events emitted from stdout.</summary>
    public ChannelReader<PiEvent> Events => _events.Reader;

    /// <summary>Writes a command without registering a correlated response waiter.</summary>
    public Task SendCommandAsync(PiCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return WriteCommandAsync(command, ct);
    }

    /// <summary>Writes a command with an ID and awaits the matching response using the default timeout.</summary>
    public Task<PiResponseEvent> SendRequestAsync(PiCommand command, CancellationToken ct)
    {
        return SendRequestAsync(command, DefaultRequestTimeout, ct);
    }

    /// <summary>Writes a command with an ID and awaits the matching response.</summary>
    public async Task<PiResponseEvent> SendRequestAsync(PiCommand command, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(command.Id))
        {
            throw new ArgumentException("Request commands must include a non-empty correlation ID.", nameof(command));
        }

        var commandType = GetCommandType(command);
        var completion = new TaskCompletionSource<PiResponseEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(command.Id, commandType, completion);

        if (!_pending.TryAdd(command.Id, pending))
        {
            throw new InvalidOperationException($"A Pi request with ID '{command.Id}' is already pending.");
        }

        try
        {
            await WriteCommandAsync(command, ct).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            if (_pending.TryRemove(command.Id, out var removed))
            {
                removed.Completion.TrySetException(new TimeoutException(
                    $"Timed out waiting for Pi response id '{command.Id}' command '{commandType}'.", ex));
            }

            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (_pending.TryRemove(command.Id, out var removed))
            {
                removed.Completion.TrySetCanceled(ct);
            }

            throw;
        }
        catch
        {
            if (_pending.TryRemove(command.Id, out var removed))
            {
                removed.Completion.TrySetCanceled(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            _pending.TryRemove(command.Id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _readerCts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _readerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during disposal.
        }

        FailPendingRequests(new OperationCanceledException("Pi JSONL client was disposed."));
        _events.Writer.TryComplete();
        _writeLock.Dispose();
        _readerCts.Dispose();
    }

    private async Task WriteCommandAsync(PiCommand command, CancellationToken ct)
    {
        var commandType = GetCommandType(command);
        var json = JsonSerializer.Serialize(command, PiJsonContext.Default.PiCommand);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        LogCommandSent(_logger, commandType, command.Id, null);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_readerCts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await _stdout.ReadLineAsync(_readerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                await HandleStdoutLineAsync(line).ConfigureAwait(false);
            }

            LogReadLoopCompleted(_logger, null);
            FailPendingRequests(new EndOfStreamException("Pi stdout closed before all pending responses completed."));
            _events.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            LogReadLoopFaulted(_logger, ex);
            FailPendingRequests(ex);
            _events.Writer.TryComplete(ex);
        }
    }

    private async Task HandleStdoutLineAsync(string line)
    {
        string? eventType;
        try
        {
            eventType = GetEventType(line);
        }
        catch (JsonException ex)
        {
            await PublishProtocolErrorAsync("malformed_json", null, null, null, ex.Message).ConfigureAwait(false);
            return;
        }

        PiEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize(NormalizeDiscriminatorsFirst(line), PiJsonContext.Default.PiEvent);
        }
        catch (JsonException ex)
        {
            await PublishProtocolErrorAsync("invalid_event", eventType, null, null, ex.Message, line).ConfigureAwait(false);
            return;
        }

        if (evt is null)
        {
            await PublishProtocolErrorAsync("null_event", eventType, null, null, "Pi stdout line deserialized to null.")
                .ConfigureAwait(false);
            return;
        }

        LogEventReceived(_logger, eventType ?? GetEventType(evt), null);

        if (evt is PiResponseEvent response)
        {
            await HandleResponseAsync(response, eventType).ConfigureAwait(false);
            return;
        }

        if (evt.GetType() == typeof(PiEvent))
        {
            await PublishProtocolErrorAsync("unknown_event_type", eventType, null, null,
                    "Pi stdout event type is not modeled.", line)
                .ConfigureAwait(false);
            return;
        }

        await _events.Writer.WriteAsync(evt, _readerCts.Token).ConfigureAwait(false);
    }

    private async Task HandleResponseAsync(PiResponseEvent response, string? eventType)
    {
        if (!string.IsNullOrWhiteSpace(response.Id))
        {
            if (_pending.TryRemove(response.Id, out var pending))
            {
                LogResponseCorrelated(_logger, response.Command, response.Id, response.Success, null);
                pending.Completion.TrySetResult(response);
                return;
            }

            await PublishProtocolErrorAsync("uncorrelated_response", eventType, response.Id, response.Command,
                    "Pi response ID does not match a pending request.")
                .ConfigureAwait(false);
            return;
        }

        if (TryCompleteUnidentifiedResponse(response))
        {
            return;
        }

        await PublishProtocolErrorAsync("response_missing_id", eventType, null, response.Command,
                "Pi response omitted its correlation ID.")
            .ConfigureAwait(false);
    }

    private bool TryCompleteUnidentifiedResponse(PiResponseEvent response)
    {
        PendingRequest? match = null;
        foreach (var pending in _pending.Values)
        {
            if (!string.Equals(pending.CommandType, response.Command, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is not null)
            {
                return false;
            }

            match = pending;
        }

        if (match is null)
        {
            return false;
        }

        if (!_pending.TryRemove(match.Id, out var removed))
        {
            return false;
        }

        LogResponseCorrelatedByCommand(_logger, response.Command, removed.Id, response.Success, null);
        removed.Completion.TrySetResult(response);
        return true;
    }

    private async Task PublishProtocolErrorAsync(
        string kind,
        string? eventType,
        string? commandId,
        string? commandType,
        string message)
    {
        await PublishProtocolErrorAsync(kind, eventType, commandId, commandType, message, rawLine: null)
            .ConfigureAwait(false);
    }

    private async Task PublishProtocolErrorAsync(
        string kind,
        string? eventType,
        string? commandId,
        string? commandType,
        string message,
        string? rawLine)
    {
        LogProtocolError(_logger, kind, eventType, commandId, null);
        if (!string.IsNullOrWhiteSpace(rawLine))
        {
            LogProtocolErrorRaw(_logger, kind, eventType, commandId, SanitizeRawLine(rawLine), null);
        }

        var errorEvent = new PiProtocolErrorEvent
        {
            Kind = kind,
            EventType = eventType,
            CommandId = commandId,
            CommandType = commandType,
            Message = message,
        };

        await _events.Writer.WriteAsync(errorEvent, _readerCts.Token).ConfigureAwait(false);
    }

    private static string NormalizeDiscriminatorsFirst(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return line;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteElementWithTypeFirst(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElementWithTypeFirst(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObjectWithTypeFirst(writer, element);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElementWithTypeFirst(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteObjectWithTypeFirst(Utf8JsonWriter writer, JsonElement element)
    {
        writer.WriteStartObject();
        if (element.TryGetProperty("type", out var type))
        {
            writer.WritePropertyName("type");
            type.WriteTo(writer);
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("type"))
            {
                continue;
            }

            writer.WritePropertyName(property.Name);
            WriteElementWithTypeFirst(writer, property.Value);
        }

        writer.WriteEndObject();
    }

    private static string SanitizeRawLine(string rawLine)
    {
        try
        {
            using var document = JsonDocument.Parse(rawLine);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteSanitizedElement(writer, document.RootElement);
            }

            return TruncateRawLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch (JsonException)
        {
            return "<malformed json omitted>";
        }
    }

    private static void WriteSanitizedElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveProperty(property.Name))
                    {
                        writer.WriteStringValue("<redacted>");
                    }
                    else
                    {
                        WriteSanitizedElement(writer, property.Value);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSanitizedElement(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveProperty(string propertyName)
    {
        return propertyName.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("access_token", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("apiKey", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("api_key", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("key", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("encryptedValue", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || propertyName.Contains("password", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateRawLine(string rawLine)
    {
        const int maxLength = 4096;
        return rawLine.Length <= maxLength
            ? rawLine
            : string.Concat(rawLine.AsSpan(0, maxLength), "<truncated>");
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var (id, pending) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private static string? GetEventType(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
    }

    private static string GetEventType(PiEvent evt)
    {
        return evt switch
        {
            PiResponseEvent => "response",
            PiAgentStartEvent => "agent_start",
            PiAgentEndEvent => "agent_end",
            PiTurnStartEvent => "turn_start",
            PiTurnEndEvent => "turn_end",
            PiMessageStartEvent => "message_start",
            PiMessageUpdateEvent => "message_update",
            PiMessageEndEvent => "message_end",
            PiToolExecutionStartEvent => "tool_execution_start",
            PiToolExecutionUpdateEvent => "tool_execution_update",
            PiToolExecutionEndEvent => "tool_execution_end",
            PiCompactionStartEvent => "compaction_start",
            PiCompactionEndEvent => "compaction_end",
            PiAutoRetryStartEvent => "auto_retry_start",
            PiAutoRetryEndEvent => "auto_retry_end",
            PiQueueUpdateEvent => "queue_update",
            PiIdleEvent => "idle",
            PiErrorEvent => "error",
            PiLogEvent => "log",
            PiSessionSwitchedEvent => "session_switched",
            PiStateUpdateEvent => "state_update",
            PiProtocolErrorEvent => "protocol_error",
            _ => "unknown",
        };
    }

    private static string GetCommandType(PiCommand command)
    {
        return command switch
        {
            PiPromptCommand => "prompt",
            PiSteerCommand => "steer",
            PiFollowUpCommand => "follow_up",
            PiAbortCommand => "abort",
            PiGetStateCommand => "get_state",
            PiGetMessagesCommand => "get_messages",
            PiSetModelCommand => "set_model",
            PiSetThinkingLevelCommand => "set_thinking_level",
            PiCompactCommand => "compact",
            PiBashCommand => "bash",
            PiNewSessionCommand => "new_session",
            PiForkCommand => "fork",
            PiCloneCommand => "clone",
            PiSwitchSessionCommand => "switch_session",
            _ => command.GetType().Name,
        };
    }

    private sealed record PendingRequest(
        string Id,
        string CommandType,
        TaskCompletionSource<PiResponseEvent> Completion);
}

/// <summary>Event emitted by <see cref="PiJsonlClient"/> when stdout violates the Pi JSONL protocol.</summary>
internal sealed record PiProtocolErrorEvent : PiEvent
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("eventType")] public string? EventType { get; init; }
    [JsonPropertyName("commandId")] public string? CommandId { get; init; }
    [JsonPropertyName("commandType")] public string? CommandType { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PiCommand))]
[JsonSerializable(typeof(PiEvent))]
[JsonSerializable(typeof(PiState))]
[JsonSerializable(typeof(PiGetMessagesResponse))]
[JsonSerializable(typeof(PiMessage[]))]
[JsonSerializable(typeof(PiResumeToken))]
[JsonSerializable(typeof(string))]
internal sealed partial class PiJsonContext : JsonSerializerContext
{
}
