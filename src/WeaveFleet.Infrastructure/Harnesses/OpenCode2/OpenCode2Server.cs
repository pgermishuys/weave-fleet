using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>Where a server sends the events of one V2 session.</summary>
internal interface IOpenCode2EventSink
{
    /// <summary>The Fleet session the events are for.</summary>
    OpenCode2SessionContext Context { get; }

    void OnEvent(OpenCode2Event evt);

    /// <summary>
    /// The event stream reconnected after a drop: catch up on what was missed. <paramref name="activeSessions"/> are
    /// the sessions with a turn running now. Runs on the event pump before any newer event is delivered.
    /// </summary>
    Task ResyncAsync(IReadOnlySet<string> activeSessions, CancellationToken ct);

    /// <summary>The server stopped; nothing more will arrive from it.</summary>
    void OnServerStopped();
}

/// <summary>
/// What Fleet starts an owner's server with: where Fleet is, the config it adds (<see cref="OpenCode2FleetFiles"/>),
/// and whether the server gets the tool for messages between sessions. When the owner changes a setting behind it, the
/// server is replaced once none of its sessions is running a turn.
/// </summary>
internal sealed record OpenCode2ServerSetup(string? FleetUrl, string? ConfigContent, bool SessionMessages)
{
    public static readonly OpenCode2ServerSetup None = new(null, null, false);
}

/// <summary>
/// One <c>opencode2 serve</c> process that Fleet runs for one owner, serving every directory: V2 takes the directory
/// per session (<c>location</c>), so there's no process per folder. Its single event stream carries every session's
/// events and is routed to the attached sessions by <c>sessionID</c>. Events for sessions nobody attached are dropped.
/// The stream is live only, so when it reconnects the attached sessions read what they missed.
/// </summary>
internal sealed partial class OpenCode2Server : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    private readonly OpenCode2ProcessManager? _process;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IOpenCode2EventSink> _sinks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;
    private TaskCompletionSource _connected = NewConnectedSource();
    private int _stopped;

    internal OpenCode2Server(
        string ownerUserId,
        OpenCode2HttpClient client,
        string bridgeToken,
        OpenCode2ProcessManager? process,
        ILogger logger,
        OpenCode2ServerSetup? setup = null)
    {
        OwnerUserId = ownerUserId;
        Client = client;
        BridgeToken = bridgeToken;
        Setup = setup ?? OpenCode2ServerSetup.None;
        _process = process;
        _logger = logger;

        if (_process is not null)
            _process.Exited += (_, _) => _ = StopRoutingAsync();

        _pump = Task.Run(PumpAsync, CancellationToken.None);
    }

    public string OwnerUserId { get; }

    public OpenCode2HttpClient Client { get; }

    /// <summary>Set as <c>FLEET_BRIDGE_TOKEN</c> in the process, and in <c>FLEET_URL</c>, so Fleet knows its calls.</summary>
    public string BridgeToken { get; }

    public OpenCode2ServerSetup Setup { get; }

    public int? ProcessId => _process?.ProcessId;

    /// <summary>False once the process exited or Fleet stopped it; a stopped server is replaced, not restarted.</summary>
    public bool IsRunning => Volatile.Read(ref _stopped) == 0 && _process?.IsRunning != false;

    /// <summary>Completes when the event stream is open, so events that follow a request aren't missed.</summary>
    public Task WaitForEventsAsync(CancellationToken ct) => Volatile.Read(ref _connected).Task.WaitAsync(ct);

    public void Attach(string harnessSessionId, IOpenCode2EventSink sink) => _sinks[harnessSessionId] = sink;

    public void Detach(string harnessSessionId, IOpenCode2EventSink sink)
        => _sinks.TryRemove(new KeyValuePair<string, IOpenCode2EventSink>(harnessSessionId, sink));

    /// <summary>The Fleet session attached to V2 session <paramref name="harnessSessionId"/> on this server, if any.</summary>
    public OpenCode2SessionContext? FindSession(string harnessSessionId)
        => _sinks.TryGetValue(harnessSessionId, out var sink) ? sink.Context : null;

    /// <summary>Whether no session on this server is running a turn; <see langword="false"/> when V2 can't say.</summary>
    public async Task<bool> IsIdleAsync(CancellationToken ct)
    {
        try
        {
            return (await Client.GetActiveSessionIdsAsync(ct).ConfigureAwait(false)).Count == 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    private async Task PumpAsync()
    {
        var ct = _stopping.Token;
        var reconnecting = false;
        while (!ct.IsCancellationRequested && IsRunning)
        {
            try
            {
                var resync = reconnecting;
                await foreach (var evt in Client.ReadEventsAsync(OnConnected, ct).ConfigureAwait(false))
                {
                    // V2 opens every stream with server.connected, so this runs as soon as the stream is back.
                    if (resync)
                    {
                        resync = false;
                        await ResyncAsync(ct).ConfigureAwait(false);
                    }

                    if (evt.SessionId is { } sessionId && _sinks.TryGetValue(sessionId, out var sink))
                        Deliver(sink, evt);
                    else if (evt.Type == "permission.asked" && evt.SessionId is { } other)
                        AllowOnce(other, evt);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogStreamFailed(_logger, ProcessId ?? 0, ex);
            }

            // The stream ended with the process still up: open it again, and make new waiters wait for that.
            // Anything sent in between is lost (V2 doesn't replay), so the sessions catch up once it's back.
            if (Volatile.Read(ref _connected).Task.IsCompleted)
            {
                Volatile.Write(ref _connected, NewConnectedSource());
                reconnecting = true;
            }

            try
            {
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// A permission ask from a session no Fleet session listens to (a subagent's child session, which doesn't get
    /// Fleet's allow-all rules) would wait forever, and so would the turn that started it. Every session on this
    /// server is Fleet's, so it's allowed once, like an attached session's.
    /// </summary>
    private void AllowOnce(string sessionId, OpenCode2Event evt)
    {
        if (!evt.Data.TryGetProperty("id", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
            return;

        var requestId = id.GetString()!;
        LogPermissionAllowed(_logger, sessionId, requestId);
        _ = ReplyAsync();

        async Task ReplyAsync()
        {
            try
            {
                await Client.ReplyToPermissionAsync(sessionId, requestId, "once", _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                LogPermissionReplyFailed(_logger, sessionId, requestId, ex);
            }
        }
    }

    private async Task ResyncAsync(CancellationToken ct)
    {
        if (_sinks.IsEmpty)
            return;

        try
        {
            var active = await Client.GetActiveSessionIdsAsync(ct).ConfigureAwait(false);
            foreach (var sink in _sinks.Values)
            {
                try
                {
                    await sink.ResyncAsync(active, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    LogSinkFailed(_logger, "resync", ex);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogResyncFailed(_logger, ProcessId ?? 0, ex);
        }
    }

    private void OnConnected()
    {
        LogConnected(_logger, ProcessId ?? 0);
        Volatile.Read(ref _connected).TrySetResult();
    }

    private void Deliver(IOpenCode2EventSink sink, OpenCode2Event evt)
    {
        try
        {
            sink.OnEvent(evt);
        }
        catch (Exception ex)
        {
            // One session's bad event must not stop the stream for every other session.
            LogSinkFailed(_logger, evt.Type, ex);
        }
    }

    /// <summary>Stops routing and tells every attached session the server is gone.</summary>
    private async Task StopRoutingAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            return;

        await _stopping.CancelAsync().ConfigureAwait(false);
        Volatile.Read(ref _connected).TrySetException(new InvalidOperationException("The OpenCode 2 server stopped."));
        foreach (var sink in _sinks.Values)
        {
            try
            {
                sink.OnServerStopped();
            }
            catch (Exception ex)
            {
                LogSinkFailed(_logger, "server stopped", ex);
            }
        }
        _sinks.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopRoutingAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        if (_process is not null)
            await _process.DisposeAsync().ConfigureAwait(false);
        Client.Dispose();
        _stopping.Dispose();
    }

    private static TaskCompletionSource NewConnectedSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [LoggerMessage(Level = LogLevel.Debug, Message = "OpenCode 2 server {ProcessId}: event stream open")]
    private static partial void LogConnected(ILogger logger, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 server {ProcessId}: event stream failed; reconnecting")]
    private static partial void LogStreamFailed(ILogger logger, int processId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 session {HarnessSessionId} (not a Fleet session's own, e.g. a subagent's) asked permission ({RequestId}); allowed once, since nobody can answer it")]
    private static partial void LogPermissionAllowed(ILogger logger, string harnessSessionId, string requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't answer OpenCode 2 permission request {RequestId} for session {HarnessSessionId}")]
    private static partial void LogPermissionReplyFailed(ILogger logger, string harnessSessionId, string requestId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenCode 2 server {ProcessId}: couldn't read the running sessions after the event stream reconnected")]
    private static partial void LogResyncFailed(ILogger logger, int processId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An OpenCode 2 session failed to handle {EventType}")]
    private static partial void LogSinkFailed(ILogger logger, string eventType, Exception exception);
}
