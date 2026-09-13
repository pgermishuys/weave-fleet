using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// The terminal WebSocket protocol.
/// <list type="bullet">
/// <item>Server → client, binary: output bytes. First the saved scrollback, then <c>{"type":"ready"}</c>, then live output.</item>
/// <item>Server → client, text: <c>{"type":"ready"}</c>, <c>{"type":"cleared"}</c>, <c>{"type":"exit","exitCode":0}</c>.</item>
/// <item>Client → server, binary: input bytes. Text: <c>{"type":"resize","cols":120,"rows":30}</c>, <c>{"type":"clear"}</c>.</item>
/// </list>
/// Close codes: 1000 after <c>exit</c> (don't reconnect); 4001 when the client fell behind and 1001 when Fleet is
/// stopping (reconnect, and the scrollback comes again).
/// </summary>
internal static class TerminalSocket
{
    public const WebSocketCloseStatus TooSlow = (WebSocketCloseStatus)4001;

    private const int ChunkSize = 16 * 1024;
    private const int MaxControlMessageBytes = 4 * 1024;

    private static readonly byte[] Ready = """{"type":"ready"}"""u8.ToArray();
    private static readonly byte[] Cleared = """{"type":"cleared"}"""u8.ToArray();

    /// <summary>
    /// Terminals are a shell, so the check is stricter than for the event hub. Browsers always send Origin on a
    /// WebSocket; a page on another port of the same machine counts as the same site and carries Fleet's cookie,
    /// so only the page Fleet served, configured origins, and (in Development) local dev servers get in.
    /// A client with no Origin isn't a browser and still has to authenticate.
    /// </summary>
    public static bool IsOriginAllowed(HttpContext http, FleetOptions options, bool development)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
            return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;
        if (string.Equals(uri.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            return true;
        if (options.Auth.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            return true;
        return development
            && (uri.Host is "localhost" or "127.0.0.1" || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task RunAsync(WebSocket socket, TerminalAttachment attachment, CancellationToken aborted)
    {
        try
        {
            for (var offset = 0; offset < attachment.Replay.Length; offset += ChunkSize)
            {
                var length = Math.Min(ChunkSize, attachment.Replay.Length - offset);
                await socket.SendAsync(attachment.Replay.AsMemory(offset, length), WebSocketMessageType.Binary, true, aborted);
            }
            await socket.SendAsync(Ready, WebSocketMessageType.Text, true, aborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            return;
        }

        // Only the output pump sends from here, so sends never overlap. Cancelling a pending receive aborts a
        // WebSocket, so the input loop is never cancelled: it ends when the client's close arrives.
        using var stopOutput = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        var output = PumpOutputAsync(socket, attachment, stopOutput.Token);
        var input = ReceiveInputAsync(socket, attachment, aborted);

        if (await Task.WhenAny(output, input) == input)
        {
            // The browser closed or went away first.
            await stopOutput.CancelAsync();
            await ((Task)output).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "Bye.");
            return;
        }

        (WebSocketCloseStatus Status, string Reason) close;
        try
        {
            close = await output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            close = (WebSocketCloseStatus.InternalServerError, "The terminal failed.");
        }

        await CloseAsync(socket, close.Status, close.Reason);

        // Let the client answer the close; give up after a moment.
        if (await Task.WhenAny(input, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)) != input)
            socket.Abort();
        await input.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(status, reason, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }

    private static async Task<(WebSocketCloseStatus Status, string Reason)> PumpOutputAsync(
        WebSocket socket,
        TerminalAttachment attachment,
        CancellationToken ct)
    {
        try
        {
            await foreach (var frame in attachment.Frames.ReadAllAsync(ct))
            {
                switch (frame.Kind)
                {
                    case TerminalFrameKind.Output:
                        await socket.SendAsync(frame.Data ?? [], WebSocketMessageType.Binary, true, ct);
                        break;
                    case TerminalFrameKind.Cleared:
                        await socket.SendAsync(Cleared, WebSocketMessageType.Text, true, ct);
                        break;
                    case TerminalFrameKind.Exited:
                        var exit = frame.ExitCode is { } code
                            ? $$"""{"type":"exit","exitCode":{{code}}}"""
                            : """{"type":"exit","exitCode":null}""";
                        await socket.SendAsync(Encoding.UTF8.GetBytes(exit), WebSocketMessageType.Text, true, ct);
                        return (WebSocketCloseStatus.NormalClosure, "The shell ended.");
                }
            }
            // Detached without an exit: the page is going, or the terminal was closed elsewhere.
            return (WebSocketCloseStatus.NormalClosure, "Detached.");
        }
        catch (TerminalClientTooSlowException)
        {
            return (TooSlow, "Fell behind the terminal's output; reconnect to catch up.");
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            return (WebSocketCloseStatus.EndpointUnavailable, "Fleet is stopping.");
        }
    }

    private static async Task ReceiveInputAsync(WebSocket socket, TerminalAttachment attachment, CancellationToken ct)
    {
        var buffer = new byte[ChunkSize];
        using var control = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // Input can go to the shell a fragment at a time; there's nothing to reassemble.
                    await attachment.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                    continue;
                }

                control.Write(buffer, 0, result.Count);
                if (control.Length > MaxControlMessageBytes)
                    return;
                if (!result.EndOfMessage)
                    continue;

                HandleControl(control.ToArray(), attachment);
                control.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }

    private static void HandleControl(byte[] json, TerminalAttachment attachment)
    {
        TerminalControlMessage? message;
        try
        {
            message = JsonSerializer.Deserialize(json, ApiJsonContext.Default.TerminalControlMessage);
        }
        catch (JsonException)
        {
            return;
        }

        switch (message?.Type)
        {
            case "resize" when message.Cols > 0 && message.Rows > 0:
                attachment.Resize(message.Cols.GetValueOrDefault(), message.Rows.GetValueOrDefault());
                break;
            case "clear":
                attachment.Clear();
                break;
        }
    }
}
