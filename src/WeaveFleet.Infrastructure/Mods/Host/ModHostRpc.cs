using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>
/// JSON-RPC 2.0 over a line stream, as <c>mods/host/src/rpc.ts</c> speaks it. Fleet sends requests and receives
/// notifications; the host's own requests go to <see cref="IModHostCalls"/> alongside the read loop, because the host
/// asks Fleet for things while Fleet waits on one of its answers.
/// </summary>
internal sealed class ModHostRpc : IAsyncDisposable
{
    private static readonly Action<ILogger, string, Exception?> LogProblem =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "ModHostRpcProblem"), "mod host connection: {Problem}");

    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly IModHostCalls _calls;
    private readonly ILogger _logger;
    private readonly int _maxLineBytes;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _lastId;
    private bool _isClosed;

    /// <summary>Starts reading <paramref name="input"/> (the host's stdout) at once; requests go to <paramref name="output"/> (its stdin).</summary>
    public ModHostRpc(Stream input, Stream output, IModHostCalls calls, ILogger logger, int maxLineBytes = 8 * 1024 * 1024)
    {
        (_input, _output, _calls, _logger, _maxLineBytes) = (input, output, calls, logger, maxLineBytes);
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Sends a request and returns its <c>result</c>. The timeout runs from this call, waiting for the write lock and the
    /// write included, so a host that has stopped reading can't hold the caller past it. After a write that timed out the
    /// pipe may hold half a line; the caller kills the host.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var id = Interlocked.Increment(ref _lastId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            if (Volatile.Read(ref _isClosed))
                throw Closed();
            await WriteAsync(Build(w =>
            {
                w.WriteNumber("id", id);
                w.WriteString("method", method);
                if (parameters is { } p)
                {
                    w.WritePropertyName("params");
                    p.WriteTo(w);
                }
            }), deadline.Token).ConfigureAwait(false);
            return await answer.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"The mod host didn't answer {method} within {timeout.TotalSeconds:0.#} seconds.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        Close();
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var line = new ArrayBufferWriter<byte>();
        var dropping = false;
        try
        {
            int read;
            while ((read = await _input.ReadAsync(buffer, _stop.Token).ConfigureAwait(false)) > 0)
            {
                for (var start = 0; start < read;)
                {
                    var newline = buffer.AsSpan(start, read - start).IndexOf((byte)'\n');
                    var end = newline < 0 ? read : start + newline;

                    // A line over the limit is thrown away as it arrives, never held.
                    if (!dropping && line.WrittenCount + (end - start) > _maxLineBytes)
                    {
                        dropping = true;
                        line.Clear();
                        LogProblem(_logger, $"a line over {_maxLineBytes} bytes was dropped", null);
                    }
                    else if (!dropping)
                    {
                        line.Write(buffer.AsSpan(start, end - start));
                    }

                    if (newline < 0)
                        break;
                    if (!dropping)
                        OnLine(line.WrittenSpan);
                    dropping = false;
                    line.Clear();
                    start = end + 1;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            // The pipe ended or Fleet is done with it.
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
        if (bytes.IsEmpty)
            return;

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(bytes.ToArray()).RootElement;
        }
        catch (JsonException)
        {
            Send(NullElement, null, ModHostErrorCodes.ParseError, "parse error: line is not valid JSON");
            return;
        }

        var id = NullElement;
        var isObject = root.ValueKind == JsonValueKind.Object;
        var hasId = isObject && root.TryGetProperty("id", out id) && id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
        if (isObject && root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            var parameters = root.TryGetProperty("params", out var p) ? p : NullElement;
            if (hasId)
                _ = Task.Run(() => AnswerAsync(id, method.GetString()!, parameters));
            else
                Notify(method.GetString()!, parameters);
        }
        else if (hasId)
        {
            OnAnswer(id, root);
        }
        else
        {
            Send(NullElement, null, ModHostErrorCodes.InvalidRequest, "not a request, notification or answer");
        }
    }

    private void OnAnswer(JsonElement id, JsonElement root)
    {
        if (!id.TryGetInt64(out var key) || !_pending.TryRemove(key, out var waiting))
            return;

        if (!root.TryGetProperty("error", out var error))
        {
            waiting.TrySetResult(root.TryGetProperty("result", out var result) ? result : NullElement);
        }
        else if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.TryGetInt32(out var number)
            && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
        {
            waiting.TrySetException(new ModHostRpcException(number, message.GetString()!, error.TryGetProperty("data", out var data) ? data : null));
        }
        else
        {
            waiting.TrySetException(new ModHostRpcException(ModHostErrorCodes.Internal, "The mod host sent a malformed error answer."));
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
            LogProblem(_logger, $"handling the {method} notification failed: {e.Message}", e);
        }
    }

    private async Task AnswerAsync(JsonElement id, string method, JsonElement parameters)
    {
        try
        {
            Send(id, await _calls.HandleRequestAsync(method, parameters, _stop.Token).ConfigureAwait(false));
        }
        catch (ModHostRpcException e)
        {
            Send(id, null, e.Code, e.Message, e.Data);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Disposed: nobody is left to answer.
        }
        catch (Exception e)
        {
            LogProblem(_logger, $"handling the {method} request failed: {e.Message}", e);
            Send(id, null, ModHostErrorCodes.Internal, e.Message);
        }
    }

    /// <summary>Answers a host request (<paramref name="result"/>, or an error when there's a code) without holding up the caller; a failed write is dropped.</summary>
    private void Send(JsonElement id, JsonElement? result, int code = 0, string message = "", JsonElement? data = null)
    {
        var line = Build(w =>
        {
            w.WritePropertyName("id");
            id.WriteTo(w);
            if (result is { } r)
            {
                w.WritePropertyName("result");
                r.WriteTo(w);
                return;
            }

            w.WriteStartObject("error");
            w.WriteNumber("code", code);
            w.WriteString("message", message);
            if (data is { } d)
            {
                w.WritePropertyName("data");
                d.WriteTo(w);
            }

            w.WriteEndObject();
        });
        _ = Task.Run(async () =>
        {
            try
            {
                await WriteAsync(line, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ModHostClosedException or OperationCanceledException)
            {
                // The pipe is gone or Fleet is done with it.
            }
        });
    }

    /// <summary>
    /// Writes one line, giving up at <paramref name="ct"/> even if the stream ignores cancellation. The write lock is held
    /// until the write really ends, so a stuck write makes the ones behind it wait on their own deadlines.
    /// </summary>
    private async Task WriteAsync(byte[] line, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        await Task.Run(async () =>
        {
            try
            {
                await _output.WriteAsync(line, ct).ConfigureAwait(false);
                await _output.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
            {
                throw Closed();
            }
            finally
            {
                _writeLock.Release();
            }
        }, CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
    }

    private void Close()
    {
        Volatile.Write(ref _isClosed, true);
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var waiting))
                waiting.TrySetException(Closed());
        }
    }

    private static ModHostClosedException Closed() => new("The mod host closed its connection.");

    /// <summary>One JSON-RPC object as UTF-8 plus the newline that ends the line.</summary>
    private static byte[] Build(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            body(writer);
            writer.WriteEndObject();
        }

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }
}
