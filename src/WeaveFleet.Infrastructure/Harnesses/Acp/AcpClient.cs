using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.Acp;

/// <summary>An error response from the agent (or the agent going away), with its JSON-RPC code.</summary>
internal sealed class AcpException(string message, int code = -32603) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// A JSON-RPC 2.0 client for an Agent Client Protocol agent: newline-delimited messages over a reader and a writer
/// (the agent's stdout and stdin). Requests go out and are matched with their responses; notifications reach
/// <see cref="Notification"/> in the order they arrived, before the response that follows them; the agent's own requests
/// (a permission ask) go to <see cref="RequestHandler"/> without holding up the reading.
/// </summary>
internal sealed partial class AcpClient : IAsyncDisposable
{
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;
    private long _nextId;
    private int _disposed;

    public AcpClient(TextReader reader, TextWriter writer, ILogger logger)
    {
        _reader = reader;
        _writer = writer;
        _logger = logger;
        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    /// <summary>A notification from the agent: its method and params.</summary>
    public event Action<string, JsonElement>? Notification;

    /// <summary>Answers a request from the agent: its method and params give the result (a JSON node) or throw.</summary>
    public Func<string, JsonElement, Task<JsonNode?>>? RequestHandler { get; set; }

    /// <summary>Raised once when the agent's output ends: the process died or closed its pipes.</summary>
    public event Action? Closed;

    public bool IsClosed { get; private set; }

    public async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;
        try
        {
            if (IsClosed)
                throw new AcpException("The agent has stopped.");
            await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, ct).ConfigureAwait(false);
            await using var _ = ct.Register(() => waiting.TrySetCanceled(ct));
            return await waiting.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out var _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct = default)
        => WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, ct);

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        var line = message.ToJsonString();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new AcpException("The agent has stopped.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                    continue;
                try
                {
                    Dispatch(JsonDocument.Parse(line).RootElement);
                }
                catch (JsonException ex)
                {
                    LogBadLine(_logger, ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The pipe closed under us.
        }

        IsClosed = true;
        foreach (var waiting in _pending.Values)
            waiting.TrySetException(new AcpException("The agent stopped before it answered."));
        Closed?.Invoke();
    }

    private void Dispatch(JsonElement message)
    {
        var hasId = message.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null;
        var method = message.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;

        if (method is null && hasId && idElement.TryGetInt64(out var responseId) && _pending.TryGetValue(responseId, out var waiting))
        {
            if (message.TryGetProperty("error", out var error))
            {
                var text = error.TryGetProperty("message", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "The agent reported an error.";
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var number) ? number : -32603;
                waiting.TrySetException(new AcpException(text ?? "The agent reported an error.", code));
            }
            else
            {
                waiting.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
            }
        }
        else if (method is not null && hasId)
        {
            _ = Task.Run(() => AnswerAsync(idElement.Clone(), method, parameters));
        }
        else if (method is not null)
        {
            Notification?.Invoke(method, parameters);
        }
    }

    private async Task AnswerAsync(JsonElement id, string method, JsonElement parameters)
    {
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        try
        {
            reply["result"] = RequestHandler is { } handler ? await handler(method, parameters).ConfigureAwait(false) : new JsonObject();
        }
        catch (Exception ex)
        {
            reply["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
        }

        try
        {
            await WriteAsync(reply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (AcpException)
        {
            // The agent is gone; nobody is waiting for the answer.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The reader was blocked on a pipe whose process is being stopped.
        }

        _cts.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The agent sent a line that isn't JSON.")]
    private static partial void LogBadLine(ILogger logger, Exception exception);
}
