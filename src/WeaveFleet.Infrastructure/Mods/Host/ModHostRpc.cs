using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>
/// JSON-RPC 2.0 over a line stream, as <c>mods/host/src/rpc.ts</c> speaks it: one JSON object per line, UTF-8, at most
/// <c>maxLineBytes</c> per line, requests, answers and notifications both ways. Fleet sends requests and receives
/// notifications; the host's own requests (<c>store.get</c>…) go to <see cref="IModHostCalls"/> and run alongside the
/// read loop, because the host asks Fleet for things while Fleet waits on a <c>dispatch</c>.
/// </summary>
internal sealed class ModHostRpc : IAsyncDisposable
{
    public const int DefaultMaxLineBytes = 8 * 1024 * 1024;

    private const int ReadBufferBytes = 64 * 1024;
    private const byte Newline = (byte)'\n';

    private static readonly Action<ILogger, string, Exception?> LogDropped =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "LineDropped"),
            "mod host sent a line over {Limit}; dropped it");

    private static readonly Action<ILogger, string, Exception?> LogBadJson =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "BadJson"),
            "mod host sent a line that is not JSON: {Reason}");

    private static readonly Action<ILogger, string, Exception?> LogStrayAnswer =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(3, "StrayAnswer"),
            "ignored an answer with no waiting request (id {Id})");

    private static readonly Action<ILogger, string, Exception?> LogNotificationFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4, "NotificationFailed"),
            "handling the mod host's {Method} notification failed");

    private static readonly Action<ILogger, string, Exception?> LogRequestFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5, "RequestFailed"),
            "handling the mod host's {Method} request failed");

    private static readonly Action<ILogger, string, Exception?> LogResultTooLarge =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(6, "ResultTooLarge"),
            "the answer to the mod host's {Method} request was over the line limit");

    private static readonly Action<ILogger, string, Exception?> LogWriteFailed =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(7, "WriteFailed"),
            "writing to the mod host failed: {Reason}");

    private static readonly Action<ILogger, string, Exception?> LogReadFailed =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(8, "ReadFailed"),
            "reading from the mod host failed: {Reason}");

    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly IModHostCalls _calls;
    private readonly ILogger _logger;
    private readonly int _maxLineBytes;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly Task _readLoop;
    private long _lastId;
    private bool _isClosed;
    private bool _disposed;

    /// <summary>Starts reading <paramref name="input"/> at once.</summary>
    /// <param name="input">The host's stdout.</param>
    /// <param name="output">The host's stdin.</param>
    public ModHostRpc(Stream input, Stream output, IModHostCalls calls, ILogger logger, int maxLineBytes = DefaultMaxLineBytes)
    {
        _input = input;
        _output = output;
        _calls = calls;
        _logger = logger;
        _maxLineBytes = maxLineBytes;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Completes once input has ended (or failed) and every waiting request has been failed.</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Sends a request and returns its <c>result</c>. An error answer throws <see cref="ModHostRpcException"/>; no answer
    /// within <paramref name="timeout"/> throws <see cref="TimeoutException"/>; a closed pipe throws
    /// <see cref="ModHostClosedException"/>.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters, TimeSpan timeout, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _lastId);
        var line = BuildRequest(id, method, parameters);
        if (line.Length - 1 > _maxLineBytes)
            throw new ModHostRpcException(ModHostErrorCodes.Internal, $"The {method} request is too large to send (over {_maxLineBytes} bytes).");

        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_isClosed)
                throw ClosedException();
            _pending[id] = answer;
        }

        try
        {
            if (!await WriteAsync(line).ConfigureAwait(false))
                throw ClosedException();
            return await answer.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A read that won't cancel ends when the process is killed; nothing here waits for it.
        }

        Close();
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[ReadBufferBytes];
        var line = new ArrayBufferWriter<byte>();
        var discarding = false;
        try
        {
            while (true)
            {
                var read = await _input.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read == 0)
                    break;

                var start = 0;
                while (start < read)
                {
                    var newline = buffer.AsSpan(start, read - start).IndexOf(Newline);
                    var end = newline < 0 ? read : start + newline;

                    // A line over the limit is counted and thrown away as it arrives, never held.
                    if (!discarding)
                    {
                        if (line.WrittenCount + (end - start) > _maxLineBytes)
                        {
                            discarding = true;
                            line.Clear();
                        }
                        else
                        {
                            line.Write(buffer.AsSpan(start, end - start));
                        }
                    }

                    if (newline < 0)
                        break;

                    if (discarding)
                    {
                        discarding = false;
                        LogDropped(_logger, $"{_maxLineBytes} bytes", null);
                        AnswerError(NullElement, ModHostErrorCodes.InvalidRequest, $"line longer than {_maxLineBytes} bytes");
                    }
                    else
                    {
                        OnLine(line.WrittenSpan);
                        line.Clear();
                    }

                    start = end + 1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            LogReadFailed(_logger, e.Message, null);
        }
        finally
        {
            Close();
        }
    }

    private void OnLine(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
            bytes = bytes[..^1];
        if (bytes.IsEmpty || IsBlank(bytes))
            return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray());
        }
        catch (JsonException e)
        {
            LogBadJson(_logger, e.Message, null);
            AnswerError(NullElement, ModHostErrorCodes.ParseError, "parse error: line is not valid JSON");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idProperty) ? idProperty : default;
            var hasId = id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
            var echoId = hasId ? id.Clone() : NullElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            {
                AnswerError(echoId, ModHostErrorCodes.InvalidRequest, "not a JSON-RPC 2.0 message");
                return;
            }

            if (root.TryGetProperty("method", out var method))
            {
                if (method.ValueKind != JsonValueKind.String)
                {
                    AnswerError(echoId, ModHostErrorCodes.InvalidRequest, "method must be a string");
                    return;
                }

                var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : NullElement;
                if (hasId)
                    StartRequest(echoId, method.GetString()!, parameters);
                else
                    Notify(method.GetString()!, parameters);
                return;
            }

            if (hasId && (root.TryGetProperty("result", out var result) | root.TryGetProperty("error", out var error)))
            {
                OnAnswer(id, result, error);
                return;
            }

            AnswerError(echoId, ModHostErrorCodes.InvalidRequest, "not a request, notification or answer");
        }
    }

    private static bool IsBlank(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
                return false;
        }

        return true;
    }

    private void OnAnswer(JsonElement id, JsonElement result, JsonElement error)
    {
        if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var key) || !_pending.TryRemove(key, out var waiting))
        {
            LogStrayAnswer(_logger, id.GetRawText(), null);
            return;
        }

        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var code) && code.TryGetInt32(out var number)
            && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            JsonElement? data = error.TryGetProperty("data", out var d) ? d.Clone() : null;
            waiting.TrySetException(new ModHostRpcException(number, message.GetString()!, data));
        }
        else if (error.ValueKind != JsonValueKind.Undefined)
        {
            waiting.TrySetException(new ModHostRpcException(ModHostErrorCodes.Internal, "The mod host sent a malformed error answer."));
        }
        else
        {
            waiting.TrySetResult(result.Clone());
        }
    }

    private void Notify(string method, JsonElement parameters)
    {
        try
        {
            _calls.HandleNotification(method, parameters);
        }
        catch (Exception e)
        {
            LogNotificationFailed(_logger, method, e);
        }
    }

    private void StartRequest(JsonElement id, string method, JsonElement parameters)
        => _ = Task.Run(() => AnswerRequestAsync(id, method, parameters));

    private async Task AnswerRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        byte[] reply;
        try
        {
            var result = await _calls.HandleRequestAsync(method, parameters, _stop.Token).ConfigureAwait(false);
            reply = BuildResult(id, result);
            if (reply.Length - 1 > _maxLineBytes)
            {
                LogResultTooLarge(_logger, method, null);
                reply = BuildError(id, ModHostErrorCodes.Internal, "result too large", null);
            }
        }
        catch (ModHostRpcException e)
        {
            reply = BuildError(id, e.Code, e.Message, e.Data);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            LogRequestFailed(_logger, method, e);
            reply = BuildError(id, ModHostErrorCodes.Internal, e.Message, null);
        }

        await WriteAsync(reply).ConfigureAwait(false);
    }

    /// <summary>Answers an error without holding up the read loop.</summary>
    private void AnswerError(JsonElement id, int code, string message)
    {
        var reply = BuildError(id, code, message, null);
        _ = Task.Run(() => WriteAsync(reply));
    }

    /// <summary>Writes one line (already ending in a newline). False when the pipe is gone.</summary>
    private async Task<bool> WriteAsync(byte[] line)
    {
        try
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _output.WriteAsync(line).ConfigureAwait(false);
                await _output.FlushAsync().ConfigureAwait(false);
                return true;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
        {
            LogWriteFailed(_logger, e.Message, null);
            return false;
        }
    }

    private void Close()
    {
        List<TaskCompletionSource<JsonElement>> waiting;
        lock (_gate)
        {
            if (_isClosed)
                return;
            _isClosed = true;
            waiting = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var request in waiting)
            request.TrySetException(ClosedException());
        _stop.Cancel();
        _closed.TrySetResult();
    }

    private static ModHostClosedException ClosedException() => new("The mod host closed its connection.");

    // ── Envelopes. Written by hand so ids, params and results are the exact JSON elements given. ──

    private static byte[] BuildRequest(long id, string method, JsonElement? parameters)
        => Build(w =>
        {
            w.WriteString("jsonrpc", "2.0");
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            if (parameters is { ValueKind: not JsonValueKind.Undefined } p)
            {
                w.WritePropertyName("params");
                p.WriteTo(w);
            }
        });

    private static byte[] BuildResult(JsonElement id, JsonElement result)
        => Build(w =>
        {
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WritePropertyName("result");
            if (result.ValueKind == JsonValueKind.Undefined)
                w.WriteNullValue();
            else
                result.WriteTo(w);
        });

    private static byte[] BuildError(JsonElement id, int code, string message, JsonElement? data)
        => Build(w =>
        {
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            id.WriteTo(w);
            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            if (data is { ValueKind: not JsonValueKind.Undefined } d)
            {
                w.WritePropertyName("data");
                d.WriteTo(w);
            }

            w.WriteEndObject();
        });

    /// <summary>One object as UTF-8 plus the newline that ends the line.</summary>
    private static byte[] Build(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        buffer.Write([Newline]);
        return buffer.WrittenSpan.ToArray();
    }
}
