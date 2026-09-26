using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>A CDP call the browser answered with an error, or a connection that died mid-call.</summary>
public sealed class CdpException(string message) : Exception(message);

/// <summary>
/// The little of the Chrome DevTools Protocol Fleet needs: send a command, await its reply, and wait for one
/// event on a page. A websocket and JSON, no package: a screenshot isn't worth a browser automation stack, and
/// everything here has to survive Native AOT. Commands carry an id; replies come back out of order, so each one
/// waits on its own promise. Every message is either a reply (has "id") or an event (has "method").
/// <para>
/// Every command gives up after <see cref="DefaultReplyTimeout"/>: a wedged browser keeps its socket open and says
/// nothing, and a caller waiting on it would wait for ever.
/// </para>
/// </summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    /// <summary>How long a command waits for its reply. A screenshot of a big page is the slowest thing Fleet asks for.</summary>
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(30);

    private readonly ClientWebSocket _socket;
    private readonly TimeSpan _replyTimeout;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _events = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reading;
    private int _id;

    private CdpConnection(ClientWebSocket socket, TimeSpan replyTimeout)
    {
        _socket = socket;
        _replyTimeout = replyTimeout;
        _reading = Task.Run(ReadAsync);
    }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    /// <param name="replyTimeout">How long a command waits for its reply; <see cref="DefaultReplyTimeout"/> when not given.</param>
    public static async Task<CdpConnection> ConnectAsync(Uri url, CancellationToken ct, TimeSpan? replyTimeout = null)
    {
        var socket = new ClientWebSocket();
        // A screenshot arrives as one base64 blob of tens of kilobytes; a bigger receive buffer than the 4 KB
        // default means fewer trips round the read loop for it.
        socket.Options.SetBuffer(64 * 1024, 4 * 1024);
        await socket.ConnectAsync(url, ct);
        return new CdpConnection(socket, replyTimeout ?? DefaultReplyTimeout);
    }

    /// <summary>
    /// Sends <paramref name="method"/> and returns its result object. <paramref name="sessionId"/> addresses a
    /// page attached with <c>Target.attachToTarget</c>; without it the command goes to the browser itself.
    /// Throws <see cref="CdpException"/> when the browser refuses it, the connection dies, or no reply comes in time.
    /// </summary>
    public async Task<JsonDocument> SendAsync(string method, Action<Utf8JsonWriter>? parameters = null, string? sessionId = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _id);
        var waiting = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (sessionId is not null)
                writer.WriteString("sessionId", sessionId);
            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                writer.WriteStartObject();
                parameters(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        await _send.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(buffer.WrittenMemory, WebSocketMessageType.Text, true, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw new CdpException($"Sending {method} to the browser failed: {error.Message}");
        }
        finally
        {
            _send.Release();
        }

        try
        {
            return await waiting.Task.WaitAsync(_replyTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw new CdpException($"The browser didn't answer {method} within {_replyTimeout.TotalSeconds:0.#} seconds.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Starts listening for one event before the command that causes it, so a fast page can't fire it first.
    /// Await the returned task, or drop it: an unawaited wait is forgotten when it's disposed.
    /// </summary>
    public EventWait Expect(string method, string sessionId) => new(this, Key(method, sessionId));

    internal sealed class EventWait(CdpConnection connection, string key) : IDisposable
    {
        private readonly TaskCompletionSource<bool> _fired = connection.Register(key);

        /// <summary>True when the event arrived, false when <paramref name="timeout"/> passed first.</summary>
        public async Task<bool> ArrivedAsync(TimeSpan timeout, CancellationToken ct)
        {
            try
            {
                await _fired.Task.WaitAsync(timeout, ct);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public void Dispose() => connection._events.TryRemove(key, out _);
    }

    private TaskCompletionSource<bool> Register(string key)
    {
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _events[key] = waiting;
        return waiting;
    }

    private static string Key(string method, string sessionId) => sessionId + "|" + method;

    private async Task ReadAsync()
    {
        var buffer = new byte[64 * 1024];
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (_socket.State == WebSocketState.Open && !_closing.IsCancellationRequested)
            {
                message.Clear();
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, _closing.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer.AsSpan(0, received.Count));
                }
                while (!received.EndOfMessage);

                // A copy: JsonDocument.Parse doesn't copy what it's given, and this buffer is about to be
                // reused for the next message while a caller is still reading its result.
                Dispatch(message.WrittenSpan.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception error)
        {
            Fail(error.Message);
            return;
        }

        Fail("The browser closed the connection.");
    }

    private void Dispatch(byte[] message)
    {
        var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var keep = false;
        try
        {
            if (root.TryGetProperty("id", out var id) && _pending.TryRemove(id.GetInt32(), out var waiting))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var text = error.TryGetProperty("message", out var detail) ? detail.GetString() : error.GetRawText();
                    waiting.TrySetException(new CdpException($"The browser refused the call: {text}"));
                    return;
                }

                keep = waiting.TrySetResult(document);
                return;
            }

            if (root.TryGetProperty("method", out var method)
                && method.GetString() is { } name
                && _events.TryRemove(Key(name, SessionOf(root)), out var expected))
            {
                expected.TrySetResult(true);
            }
        }
        finally
        {
            // The result document is the caller's to read and dispose; everything else is done with here.
            if (!keep)
                document.Dispose();
        }
    }

    private static string SessionOf(JsonElement root)
        => root.TryGetProperty("sessionId", out var session) ? session.GetString() ?? string.Empty : string.Empty;

    private void Fail(string reason)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiting))
                waiting.TrySetException(new CdpException(reason));
        }

        foreach (var key in _events.Keys)
        {
            if (_events.TryRemove(key, out var waiting))
                waiting.TrySetResult(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception error) when (error is WebSocketException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            // The browser is going away anyway.
        }

        Fail("The connection to the browser was closed.");
        _socket.Dispose();
        _send.Dispose();
        try
        {
            await _reading.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            // The read loop is stuck on a dead socket; it goes with the process.
        }

        _closing.Dispose();
    }
}
